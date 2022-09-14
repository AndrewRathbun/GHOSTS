﻿// Copyright 2017 Carnegie Mellon University. All Rights Reserved. See LICENSE.md file for terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using ghosts.client.linux.Infrastructure;
using ghosts.client.linux.timelineManager;
using Ghosts.Domain;
using Ghosts.Domain.Code;
using Ghosts.Domain.Messages.MesssagesForServer;
using NLog;
using Newtonsoft.Json;

namespace ghosts.client.linux.Comms
{
    /// <summary>
    /// Get updates from the C2 server - could be timeline, health, etc.
    /// </summary>
    public static class Updates
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Threaded calls to C2 for updates and to post this client's results of activity
        /// </summary>
        public static void Run()
        {
            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                GetServerUpdates();
            }).Start();

            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                PostClientResults();
            }).Start();
        }

        private static void GetServerUpdates()
        {
            if (!Program.Configuration.ClientUpdates.IsEnabled)
                return;

            // ignore all certs
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            var machine = new ResultMachine();

            Thread.Sleep(Jitter.Basic(Program.Configuration.ClientUpdates.CycleSleep));

            while (true)
            {
                try
                {
                    var s = string.Empty;
                    using (var client = WebClientBuilder.Build(machine))
                    {
                        try
                        {
                            using (var reader =
                                new StreamReader(client.OpenRead(Program.Configuration.ClientUpdates.PostUrl)))
                            {
                                s = reader.ReadToEnd();
                                _log.Debug($"{DateTime.Now} - Received new configuration");
                            }
                        }
                        catch (WebException wex)
                        {
                            if (wex?.Response == null)
                            {
                                _log.Debug($"{DateTime.Now} - API Server appears to be not responding");
                            }
                            else if (((HttpWebResponse)wex.Response).StatusCode == HttpStatusCode.NotFound)
                            {
                                _log.Debug($"{DateTime.Now} - No new configuration found");
                            }
                        }
                        catch (Exception e)
                        {
                            _log.Error($"Exception in connecting to server: {e.Message}");
                        }
                    }

                    if (!string.IsNullOrEmpty(s))
                    {
                        var update = JsonConvert.DeserializeObject<UpdateClientConfig>(s);

                        switch (update.Type)
                        {
                            case UpdateClientConfig.UpdateType.RequestForTimeline:
                                PostCurrentTimeline(update);
                                break;
                            case UpdateClientConfig.UpdateType.Timeline:
                                TimelineBuilder.SetLocalTimeline(update.Update.ToString());
                                break;
                            case UpdateClientConfig.UpdateType.TimelinePartial:
                                try
                                {
                                    var timeline = JsonConvert.DeserializeObject<Timeline>(update.Update.ToString());

                                    foreach (var timelineHandler in timeline.TimeLineHandlers)
                                    {
                                        _log.Trace($"PartialTimeline found: {timelineHandler.HandlerType}");

                                        foreach (var timelineEvent in timelineHandler.TimeLineEvents)
                                        {
                                            if (string.IsNullOrEmpty(timelineEvent.TrackableId))
                                            {
                                                timelineEvent.TrackableId = Guid.NewGuid().ToString();
                                            }
                                        }

                                        Orchestrator.RunCommand(timeline, timelineHandler);
                                    }
                                }
                                catch (Exception exc)
                                {
                                    _log.Debug(exc);
                                }

                                break;
                            case UpdateClientConfig.UpdateType.Health:
                            {
                                var newTimeline = JsonConvert.DeserializeObject<ResultHealth>(update.Update.ToString());
                                //save to local disk
                                using (var file = File.CreateText(ApplicationDetails.ConfigurationFiles.Health))
                                {
                                    var serializer = new JsonSerializer {Formatting = Formatting.Indented};
                                    serializer.Serialize(file, newTimeline);
                                }

                                break;
                            }
                            default:
                                _log.Debug($"Update {update.Type} has no handler, ignoring...");
                                break;
                        }
                    }
                }
                catch (Exception e)
                {
                    _log.Debug("Problem polling for new configuration");
                    _log.Error(e);
                }

                Thread.Sleep(Jitter.Basic(Program.Configuration.ClientUpdates.CycleSleep));
            }
        }

        private static void PostCurrentTimeline(UpdateClientConfig update)
        {
            // is the config for a specific timeline id?
            var timelineId = TimelineUpdateClientConfigManager.GetConfigUpdateTimelineId(update);

            // get all timelines
            var localTimelines = TimelineManager.GetLocalTimelines();

            var timelines = localTimelines as Timeline[] ?? localTimelines.ToArray();
            if (timelineId != Guid.Empty)
            {
                foreach (var timeline in timelines)
                {
                    if (timeline.Id == timelineId)
                    {
                        timelines = new List<Timeline>()
                        {
                            timeline
                        }.ToArray();
                        break;
                    }
                }
            }

            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            string postUrl;

            try
            {
                postUrl = Program.Configuration.IdUrl.Replace("clientid", "clienttimeline");
            }
            catch
            {
                _log.Error("Can't get timeline posturl!");
                return;
            }

            foreach (var timeline in timelines)
            {
                try
                {
                    _log.Trace("posting timeline");

                    var payload = TimelineBuilder.TimelineToString(timeline);
                    var machine = new ResultMachine();
                    
                    using (var client = WebClientBuilder.Build(machine))
                    {
                        client.Headers[HttpRequestHeader.ContentType] = "application/json";
                        client.UploadString(postUrl, JsonConvert.SerializeObject(payload));
                    }

                    _log.Trace($"{DateTime.Now} - timeline posted to server successfully");
                }
                catch (Exception e)
                {
                    _log.Debug(
                        $"Problem posting timeline to server from {ApplicationDetails.ConfigurationFiles.Timeline} to {postUrl}");
                    _log.Error(e);
                }
            }
        }

        private static void PostClientResults()
        {
            if (!Program.Configuration.ClientResults.IsEnabled)
                return;

            // ignore all certs
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            var fileName = ApplicationDetails.LogFiles.ClientUpdates;
            var postUrl = Program.Configuration.ClientResults.PostUrl;

            var machine = new ResultMachine();
            
            Thread.Sleep(Jitter.Basic(Program.Configuration.ClientResults.CycleSleep));

            while (true)
            {
                try
                {
                    if (File.Exists(fileName))
                    {
                        PostResults(fileName, machine, postUrl);
                        _log.Trace($"{fileName} posted successfully...");
                    }
                    else
                    {
                        _log.Trace($"{DateTime.Now} - {fileName} not found - sleeping...");
                    }
                }
                catch (Exception e)
                {
                    _log.Error($"Problem posting logs to server: {e.Message}");
                }
                finally
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                // look for other result files that have not been posted
                try
                {
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fileName), "*.log" ?? throw new InvalidOperationException("Path declaration failed")))
                    {
                        if (!file.EndsWith("app.log") && file != fileName)
                        {
                            PostResults(file, machine, postUrl, true);
                            _log.Trace($"{fileName} posted successfully...");
                        }
                    }
                }
                catch (Exception e)
                {
                    _log.Debug($"Problem posting overflow logs from {fileName} to server {postUrl} : {e.Message}");
                }
                finally
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                Thread.Sleep(Jitter.Basic(Program.Configuration.ClientResults.CycleSleep));
            }
        }

        private static void PostResults(string fileName, ResultMachine machine, string postUrl, bool isDeletable = false)
        {
            var tempFile = $"{fileName.Replace("clientupdates.log", Guid.NewGuid().ToString())}.proc";
            File.Copy(fileName, tempFile);
            File.WriteAllText(fileName, "");
            
            var rawLogContents = File.ReadAllText(tempFile);
            
            try
            {
                var r = new TransferLogDump {Log = rawLogContents};
                var payload = JsonConvert.SerializeObject(r);
                if (Program.Configuration.ClientResults.IsSecure)
                {
                    payload = Crypto.EncryptStringAes(payload, machine.Name);
                    payload = Base64Encoder.Base64Encode(payload);

                    var p = new EncryptedPayload {Payload = payload};

                    payload = JsonConvert.SerializeObject(p);
                }

                using (var client = WebClientBuilder.Build(machine))
                {
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";
                    client.UploadString(postUrl, payload);
                }
            }
            catch (Exception e)
            {
                try
                {
                    //put the temp file contents back
                    File.AppendAllText(fileName, rawLogContents);
                    File.Delete(tempFile);
                }
                catch 
                { 
                    _log.Trace($"Log post failure cleanup also failed: {e}");
                }
                throw;
            }
            
            //delete the temp file we used for reading
            File.Delete(tempFile);
            
            if (isDeletable)
            { 
                File.Delete(fileName);
            }
            else
            {
                File.WriteAllText(fileName, "");
            }

            _log.Trace($"{DateTime.Now} - {fileName} posted to server successfully");
        }

        internal static void PostSurvey()
        {
            // ignore all certs
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            string postUrl;

            try
            {
                postUrl = Program.Configuration.Survey.PostUrl;
            }
            catch
            {
                _log.Error("Can't get survey posturl!");
                return;
            }

            try
            {
                _log.Trace("posting survey");

                Thread.Sleep(Jitter.Basic(100));

                if (!File.Exists(ApplicationDetails.InstanceFiles.SurveyResults))
                    return;

                var survey = JsonConvert.DeserializeObject<Ghosts.Domain.Messages.MesssagesForServer.Survey>(File.ReadAllText(ApplicationDetails.InstanceFiles.SurveyResults));

                var payload = JsonConvert.SerializeObject(survey);

                var machine = new ResultMachine();
 
                if (Program.Configuration.Survey.IsSecure)
                {
                    payload = Crypto.EncryptStringAes(payload, machine.Name);
                    payload = Base64Encoder.Base64Encode(payload);

                    var p = new EncryptedPayload {Payload = payload};

                    payload = JsonConvert.SerializeObject(p);
                }

                using (var client = WebClientBuilder.Build(machine))
                {
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";
                    client.UploadString(postUrl, payload);
                }

                _log.Trace($"{DateTime.Now} - survey posted to server successfully");

                File.Delete(ApplicationDetails.InstanceFiles.SurveyResults);
            }
            catch (Exception e)
            {
                _log.Debug($"Problem posting logs to server from { ApplicationDetails.InstanceFiles.SurveyResults } to { Program.Configuration.Survey.PostUrl }");
                _log.Error(e);
            }
        }
    }
}