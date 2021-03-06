﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Kudu.Contracts.Jobs;
using Kudu.Contracts.Settings;
using Kudu.Core.Hooks;
using Kudu.Core.Infrastructure;
using Kudu.Core.Tracing;

namespace Kudu.Core.Jobs
{
    public class TriggeredJobRunner : BaseJobRunner, IDisposable
    {
        private ManualResetEvent _currentRunningJobWaitHandle;
        private readonly LockFile _lockFile;

        public TriggeredJobRunner(string jobName, IEnvironment environment, IDeploymentSettingsManager settings, ITraceFactory traceFactory, IAnalytics analytics)
            : base(jobName, Constants.TriggeredPath, environment, settings, traceFactory, analytics)
        {
            _lockFile = BuildTriggeredJobRunnerLockFile(JobDataPath, TraceFactory);
        }

        public WaitHandle CurrentRunningJobWaitHandle
        {
            get { return _currentRunningJobWaitHandle; }
        }

        public static LockFile BuildTriggeredJobRunnerLockFile(string jobDataPath, ITraceFactory traceFactory)
        {
            return new LockFile(Path.Combine(jobDataPath, "triggeredJob.lock"), traceFactory);
        }

        protected override string JobEnvironmentKeyPrefix
        {
            get { return "WEBSITE_TRIGGERED_JOB_RUNNING_"; }
        }

        protected override TimeSpan IdleTimeout
        {
            get { return Settings.GetWebJobsIdleTimeout(); }
        }

        public void StartJobRun(TriggeredJob triggeredJob, Action<string, string> reportAction)
        {
            if (Settings.IsWebJobsStopped())
            {
                throw new WebJobsStoppedException();
            }

            if (!_lockFile.Lock())
            {
                throw new ConflictException();
            }

            TriggeredJobRunLogger logger = TriggeredJobRunLogger.LogNewRun(triggeredJob, Environment, TraceFactory, Settings);
            Debug.Assert(logger != null);

            try
            {
                InitializeJobInstance(triggeredJob, logger);

                if (_currentRunningJobWaitHandle != null)
                {
                    _currentRunningJobWaitHandle.Dispose();
                    _currentRunningJobWaitHandle = null;
                }

                _currentRunningJobWaitHandle = new ManualResetEvent(false);

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        RunJobInstance(triggeredJob, logger, logger.Id);
                    }
                    finally
                    {
                        logger.ReportEndRun();
                        _lockFile.Release();
                        reportAction(triggeredJob.Name, logger.Id);
                        _currentRunningJobWaitHandle.Set();
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to start job due to: " + ex);
                _lockFile.Release();
                throw;
            }
        }

        protected override string RefreshShutdownNotificationFilePath(string jobName, string jobsTypePath)
        {
            // Since we don't use the shutdown notification file for triggered WebJobs we return null
            return null;
        }

        protected override void UpdateStatus(IJobLogger logger, string status)
        {
            ((TriggeredJobRunLogger)logger).ReportStatus(status);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_currentRunningJobWaitHandle != null)
                {
                    _currentRunningJobWaitHandle.Dispose();
                    _currentRunningJobWaitHandle = null;
                }
            }
        }
    }
}
