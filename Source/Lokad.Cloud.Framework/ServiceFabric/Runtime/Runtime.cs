﻿#region Copyright (c) Lokad 2009-2011
// This code is released under the terms of the new BSD licence.
// URL: http://www.lokad.com/
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autofac;
using Autofac.Configuration;
using Lokad.Cloud.Diagnostics;
using Lokad.Cloud.Instrumentation;
using Lokad.Cloud.Runtime;

namespace Lokad.Cloud.ServiceFabric.Runtime
{
    /// <summary>Organize the executions of the services.</summary>
    internal class Runtime
    {
        readonly RuntimeProviders _runtimeProviders;
        readonly IRuntimeFinalizer _runtimeFinalizer;
        readonly ILog _log;
        readonly ICloudRuntimeObserver _observer;

        readonly ICloudEnvironment _environment;
        readonly CloudConfigurationSettings _settings;

        /// <summary>Main thread used to schedule services in <see cref="Execute()"/>.</summary>
        Thread _executeThread;

        volatile bool _isStopRequested;
        Scheduler _scheduler;
        IRuntimeFinalizer _applicationFinalizer;

        /// <summary>Container used to populate cloud service properties.</summary>
        public IContainer RuntimeContainer { get; set; }

        /// <summary>IoC constructor.</summary>
        public Runtime(RuntimeProviders runtimeProviders, ICloudEnvironment environment, CloudConfigurationSettings settings, ICloudRuntimeObserver observer = null)
        {
            _runtimeProviders = runtimeProviders;
            _runtimeFinalizer = runtimeProviders.RuntimeFinalizer;
            _environment = environment;
            _log = runtimeProviders.Log;
            _observer = observer;

            _settings = settings;
        }

        /// <summary>Called once by the service fabric. Call is not supposed to return
        /// until stop is requested, or an uncaught exception is thrown.</summary>
        public void Execute()
        {
            _log.DebugFormat("Runtime: started on worker {0}.", _environment.WorkerName);

            // hook on the current thread to force shut down
            _executeThread = Thread.CurrentThread;

            IContainer applicationContainer = null;
            try
            {
                List<CloudService> services;

                // note: we need to keep the container alive until the finally block
                // because the finalizer (of the container) is called there.
                applicationContainer = LoadAndBuildApplication(out services);

                _applicationFinalizer = applicationContainer.ResolveOptional<IRuntimeFinalizer>();
                _scheduler = new Scheduler(services, service => service.Start(), _observer);

                foreach (var action in _scheduler.Schedule())
                {
                    if (_isStopRequested)
                    {
                        break;
                    }

                    action();
                }
            }
            catch (ThreadInterruptedException)
            {
                _log.WarnFormat("Runtime: execution was interrupted on worker {0} in service {1}. The Runtime will be restarted.",
                    _environment.WorkerName, GetNameOfServiceInExecution());
            }
            catch (ThreadAbortException)
            {
                Thread.ResetAbort();

                _log.DebugFormat("Runtime: execution was aborted on worker {0} in service {1}. The Runtime is stopping.",
                    _environment.WorkerName, GetNameOfServiceInExecution());
            }
            catch (TimeoutException)
            {
                _log.WarnFormat("Runtime: execution timed out on worker {0} in service {1}. The Runtime will be restarted.",
                    _environment.WorkerName, GetNameOfServiceInExecution());
            }
            catch (TriggerRestartException)
            {
                // Supposed to be handled by the runtime host (i.e. SingleRuntimeHost)
                throw;
            }
            catch (Exception ex)
            {
                _log.ErrorFormat(ex, "Runtime: An unhandled {0} exception occurred on worker {1} in service {2}. The Runtime will be restarted.",
                    ex.GetType().Name, _environment.WorkerName, GetNameOfServiceInExecution());
            }
            finally
            {
                _log.DebugFormat("Runtime: stopping on worker {0}.", _environment.WorkerName);

                if (_runtimeFinalizer != null)
                {
                    _runtimeFinalizer.FinalizeRuntime();
                }

                if (_applicationFinalizer != null)
                {
                    _applicationFinalizer.FinalizeRuntime();
                }

                if (applicationContainer != null)
                {
                    applicationContainer.Dispose();
                }

                _log.DebugFormat("Runtime: stopped on worker {0}.", _environment.WorkerName);
            }
        }

        /// <summary>The name of the service that is being executed, if any, <c>null</c> otherwise.</summary>
        private string GetNameOfServiceInExecution()
        {
            var scheduler = _scheduler;
            CloudService service;
            if (scheduler == null || (service = scheduler.CurrentlyScheduledService) == null)
            {
                return "unknown";
            }

            return service.Name;
        }

        /// <summary>Stops all services at once.</summary>
        /// <remarks>Called once by the service fabric when environment is about to
        /// be shut down.</remarks>
        public void Stop()
        {
            _isStopRequested = true;
            _log.DebugFormat("Runtime: Stop() on worker {0}.", _environment.WorkerName);

            if (_executeThread != null)
            {
                _executeThread.Abort();
                return;
            }

            if (_scheduler != null)
            {
                _scheduler.AbortWaitingSchedule();
            }
        }

        /// <summary>
        /// Load and get all initialized service instances using the provided IoC container.
        /// </summary>
        IContainer LoadAndBuildApplication(out List<CloudService> services)
        {
            var applicationBuilder = new ContainerBuilder();
            applicationBuilder.RegisterModule(new CloudModule());
            applicationBuilder.RegisterInstance(_settings);

            // Load Application Assemblies into the AppDomain
            var loader = new AssemblyLoader(_runtimeProviders);
            loader.LoadPackage();

            // Load Application IoC Configuration and apply it to the builder
            var config = loader.LoadConfiguration();
            if (config.HasValue)
            {
                // HACK: need to copy settings locally first
                // HACK: hard-code string for local storage name
                const string fileName = "lokad.cloud.clientapp.config";
                const string resourceName = "LokadCloudStorage";

                var pathToFile = Path.Combine(_environment.GetLocalResourcePath(resourceName), fileName);
                File.WriteAllBytes(pathToFile, config.Value);
                applicationBuilder.RegisterModule(new ConfigurationSettingsReader("autofac", pathToFile));
            }

            // Look for all cloud services currently loaded in the AppDomain
            var serviceTypes = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetExportedTypes()).SelectMany(x => x)
                .Where(t => t.IsSubclassOf(typeof (CloudService)) && !t.IsAbstract && !t.IsGenericType)
                .ToList();

            // Register the cloud services in the IoC Builder so we can support dependencies
            foreach (var type in serviceTypes)
            {
                applicationBuilder.RegisterType(type)
                    .OnActivating(e =>
                        {
                            e.Context.InjectUnsetProperties(e.Instance);

                            var initializable = e.Instance as IInitializable;
                            if (initializable != null)
                            {
                                initializable.Initialize();
                            }
                        })
                    .InstancePerDependency()
                    .ExternallyOwned();

                // ExternallyOwned: to prevent the container from disposing the
                // cloud services - we manage their lifetime on our own using
                // e.g. RuntimeFinalizer
            }

            var applicationContainer = applicationBuilder.Build();

            // Instanciate and return all the cloud services
            services = serviceTypes.Select(type => (CloudService)applicationContainer.Resolve(type)).ToList();

            return applicationContainer;
        }
    }
}