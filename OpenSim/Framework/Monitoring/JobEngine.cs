/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using log4net;
using OpenSim.Framework;

namespace OpenSim.Framework.Monitoring
{
    public class Job
    {
        public string Name;
        public WaitCallback Callback;
        public object O;

        public Job(string name, WaitCallback callback, object o)
        {
            Name = name;
            Callback = callback;
            O = o;
        }
    }

    public class JobEngine
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public bool IsRunning { get; private set; } 

        /// <summary>
        /// The timeout in milliseconds to wait for at least one event to be written when the recorder is stopping.
        /// </summary>
        public int RequestProcessTimeoutOnStop { get; set; }

        /// <summary>
        /// Controls whether we need to warn in the log about exceeding the max queue size.
        /// </summary>
        /// <remarks>
        /// This is flipped to false once queue max has been exceeded and back to true when it falls below max, in 
        /// order to avoid spamming the log with lots of warnings.
        /// </remarks>
        private bool m_warnOverMaxQueue = true;

        private BlockingCollection<Job> m_requestQueue;

        private CancellationTokenSource m_cancelSource = new CancellationTokenSource();

        private Stat m_requestsWaitingStat;

        private Job m_currentJob;

        /// <summary>
        /// Used to signal that we are ready to complete stop.
        /// </summary>
        private ManualResetEvent m_finishedProcessingAfterStop = new ManualResetEvent(false);

        public JobEngine()
        {
            RequestProcessTimeoutOnStop = 5000;

            MainConsole.Instance.Commands.AddCommand(
                "Debug",
                false,
                "debug jobengine",
                "debug jobengine <start|stop|status>",
                "Start, stop or get status of the job engine.",
                "If stopped then all jobs are processed immediately.",
                HandleControlCommand);
        }

        public void Start()
        {
            lock (this)
            {
                if (IsRunning)
                    return;

                IsRunning = true;

                m_finishedProcessingAfterStop.Reset();

                m_requestQueue = new BlockingCollection<Job>(new ConcurrentQueue<Job>(), 5000);

                m_requestsWaitingStat = 
                    new Stat(
                        "JobsWaiting",
                        "Number of jobs waiting for processing.",
                        "",
                        "",
                        "server",
                        "jobengine",
                        StatType.Pull,
                        MeasuresOfInterest.None,
                        stat => stat.Value = m_requestQueue.Count,
                        StatVerbosity.Debug);

                StatsManager.RegisterStat(m_requestsWaitingStat);

                Watchdog.StartThread(
                    ProcessRequests,
                    "JobEngineThread",
                    ThreadPriority.Normal,
                    false,
                    true,
                    null,
                    int.MaxValue);
            }
        }

        public void Stop()
        {   
            lock (this)
            {
                try
                {
                    if (!IsRunning)
                        return;

                    IsRunning = false;

                    int requestsLeft = m_requestQueue.Count;

                    if (requestsLeft <= 0)
                    {
                        m_cancelSource.Cancel();
                    }
                    else 
                    {
                        m_log.InfoFormat("[JOB ENGINE]: Waiting to write {0} events after stop.", requestsLeft);

                        while (requestsLeft > 0)
                        {
                            if (!m_finishedProcessingAfterStop.WaitOne(RequestProcessTimeoutOnStop))
                            {
                                // After timeout no events have been written
                                if (requestsLeft == m_requestQueue.Count)
                                {
                                    m_log.WarnFormat(
                                        "[JOB ENGINE]: No requests processed after {0} ms wait.  Discarding remaining {1} requests", 
                                        RequestProcessTimeoutOnStop, requestsLeft);

                                    break;
                                }
                            }

                            requestsLeft = m_requestQueue.Count;
                        }
                    }
                }
                finally
                {
                    m_cancelSource.Dispose();
                    StatsManager.DeregisterStat(m_requestsWaitingStat);
                    m_requestsWaitingStat = null;
                    m_requestQueue = null;
                }
            }
        }

        public bool QueueRequest(string name, WaitCallback req, object o)
        {
            m_log.DebugFormat("[JOB ENGINE]: Queued job {0}", name);

            if (m_requestQueue.Count < m_requestQueue.BoundedCapacity)
            {
                //                m_log.DebugFormat(
                //                    "[OUTGOING QUEUE REFILL ENGINE]: Adding request for categories {0} for {1} in {2}", 
                //                    categories, client.AgentID, m_udpServer.Scene.Name);

                m_requestQueue.Add(new Job(name, req, o));

                if (!m_warnOverMaxQueue)
                    m_warnOverMaxQueue = true;

                return true;
            }
            else
            {
                if (m_warnOverMaxQueue)
                {
//                    m_log.WarnFormat(
//                        "[JOB ENGINE]: Request queue at maximum capacity, not recording request from {0} in {1}", 
//                        client.AgentID, m_udpServer.Scene.Name);

                    m_log.WarnFormat("[JOB ENGINE]: Request queue at maximum capacity, not recording job");

                    m_warnOverMaxQueue = false;
                }

                return false;
            }
        }

        private void ProcessRequests()
        {
            try
            {
                while (IsRunning || m_requestQueue.Count > 0)
                {
                    m_currentJob = m_requestQueue.Take(m_cancelSource.Token);

                    //                QueueEmpty callback = req.Client.OnQueueEmpty;                      
                    //
                    //                if (callback != null)
                    //                {               
                    //                    try 
                    //                    { 
                    //                        callback(req.Categories); 
                    //                    }
                    //                    catch (Exception e) 
                    //                    { 
                    //                        m_log.Error("[OUTGOING QUEUE REFILL ENGINE]: ProcessRequests(" + req.Categories + ") threw an exception: " + e.Message, e); 
                    //                    }
                    //                }

                    m_log.DebugFormat("[JOB ENGINE]: Processing job {0}", m_currentJob.Name);
                    m_currentJob.Callback.Invoke(m_currentJob.O);
                    m_log.DebugFormat("[JOB ENGINE]: Processed job {0}", m_currentJob.Name);
                    m_currentJob = null;
                }
            }
            catch (OperationCanceledException)
            {
            }

            m_finishedProcessingAfterStop.Set();
        }

        private void HandleControlCommand(string module, string[] args)
        {
//            if (SceneManager.Instance.CurrentScene != null && SceneManager.Instance.CurrentScene != m_udpServer.Scene)
//                return;

            if (args.Length != 3)
            {
                MainConsole.Instance.Output("Usage: debug jobengine <stop|start|status>");
                return;
            }

            string subCommand = args[2];

            if (subCommand == "stop")
            {
                Stop();
                MainConsole.Instance.OutputFormat("Stopped job engine.");
            }
            else if (subCommand == "start")
            {
                Start();
                MainConsole.Instance.OutputFormat("Started job engine.");
            }
            else if (subCommand == "status")
            {
                MainConsole.Instance.OutputFormat("Job engine running: {0}", IsRunning);
                MainConsole.Instance.OutputFormat("Current job {0}", m_currentJob != null ? m_currentJob.Name : "none");
                MainConsole.Instance.OutputFormat(
                    "Jobs waiting: {0}", IsRunning ? m_requestQueue.Count.ToString() : "n/a");
            }
            else 
            {
                MainConsole.Instance.OutputFormat("Unrecognized job engine subcommand {0}", subCommand);
            }
        }
    }
}