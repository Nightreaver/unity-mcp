using System;
using System.IO;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Watches for external MCP session-start triggers on a running Editor:
    /// 1. A trigger file in ~/.unity-mcp (CLI: "unity-mcp instance connect") — equivalent
    ///    to clicking Connect / Start Session in the MCP window. The file may optionally
    ///    contain a project name or project-hash prefix to target one instance; an empty
    ///    file triggers the first disconnected instance that sees it.
    /// 2. "Auto-Connect When Server Starts" in Advanced Settings (HTTP Local): connects
    ///    whenever the local server is reachable but no session is running, so a session
    ///    dropped while the server stayed up heals itself. A manual End Session is respected
    ///    (<see cref="ManualSessionEndKey"/>) until the user starts a session again or the
    ///    server comes back up after being unreachable (3 consecutive failed probes, then a
    ///    reachable one). Skipped while compiling/importing, while a reload-resume
    ///    (<see cref="HttpBridgeReloadHandler"/>) or an interrupted auto-start reconnect
    ///    (<see cref="HttpAutoStartHandler"/>) still owns bridge revival, and for a back-off
    ///    after a failed start. Racing another start is safe: TransportManager coalesces
    ///    concurrent StartAsync calls per mode.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConnectTriggerWatcher
    {
        private const double PollIntervalSeconds = 1.0;
        private const double TriggerMaxAgeSeconds = 15.0;
        private const double ReachabilityPollIntervalSeconds = 3.0;
        private const double FailedStartBackoffSeconds = 15.0;
        // A single (250 ms) connect probe can fail after a main-thread stall; only this many in a row mark
        // the server as down (so the next reachable probe counts as a restart).
        private const int UnreachableProbesForServerDown = 3;
        private const string ServerWasReachableKey = "ConnectTriggerWatcher.ServerWasReachable";

        /// <summary>
        /// SessionState flag: the user ended the session from the MCP window. Survives domain
        /// reloads, not editor restarts. Set by McpConnectionSection, cleared when a session is
        /// started deliberately (window toggle, Start Server, connect trigger) or the server
        /// comes back up (rising edge).
        /// </summary>
        internal const string ManualSessionEndKey = "ConnectTriggerWatcher.ManualSessionEnd";

        private static double lastReachabilityPollTime;
        private static double autoConnectNotBefore;
        private static int consecutiveUnreachableProbes;

        private static readonly string TriggerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unity-mcp",
            "connect.trigger");

        private static double lastPollTime;
        private static bool connectInProgress;

        static ConnectTriggerWatcher()
        {
            if (Application.isBatchMode &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH")))
            {
                return;
            }
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (connectInProgress || now - lastPollTime < PollIntervalSeconds)
            {
                return;
            }
            lastPollTime = now;

            PollAutoConnect(now);
            if (connectInProgress)
            {
                return;
            }

            try
            {
                if (!File.Exists(TriggerPath))
                {
                    return;
                }

                double ageSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(TriggerPath)).TotalSeconds;
                if (ageSeconds > TriggerMaxAgeSeconds)
                {
                    File.Delete(TriggerPath);
                    return;
                }

                string target = File.ReadAllText(TriggerPath).Trim();
                if (!string.IsNullOrEmpty(target) && !MatchesThisProject(target))
                {
                    return;
                }

                if (MCPServiceLocator.Bridge.IsRunning)
                {
                    // Already connected: consume only if explicitly targeted at this project,
                    // otherwise leave the trigger for a not-yet-connected instance.
                    if (!string.IsNullOrEmpty(target))
                    {
                        File.Delete(TriggerPath);
                    }
                    return;
                }

                File.Delete(TriggerPath);
                SessionState.EraseBool(ManualSessionEndKey);
                // Same as clicking Connect: take over bridge lifecycle so a pending reload-resume
                // or interrupted auto-start reconnect cannot bounce the session we establish.
                HttpBridgeReloadHandler.CancelPendingResume();
                HttpAutoStartHandler.CancelPendingReconnect();
                connectInProgress = true;
                McpLog.Info("Connect trigger received; starting MCP session.");
                _ = StartSessionAsync();
            }
            catch (Exception ex)
            {
                McpLog.Debug($"Connect trigger poll failed: {ex.Message}");
            }
        }

        private static void PollAutoConnect(double now)
        {
            if (now - lastReachabilityPollTime < ReachabilityPollIntervalSeconds)
            {
                return;
            }
            lastReachabilityPollTime = now;

            try
            {
                if (!EditorPrefs.GetBool(EditorPrefKeys.AutoConnectOnServerStart, false))
                {
                    return;
                }
                if (!EditorConfigurationCache.Instance.UseHttpTransport || HttpEndpointUtility.IsRemoteScope())
                {
                    return;
                }

                // Checked before probing so a server (re)start during the skip is still seen
                // as a rising edge afterwards. A pending reload-resume or auto-start reconnect
                // owns bridge revival; never race it.
                if (HttpBridgeReloadHandler.IsEditorBusy()
                    || HttpBridgeReloadHandler.IsResumePending
                    || SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false)
                    || now < autoConnectNotBefore)
                {
                    return;
                }

                // Already connected: nothing to heal, and the probe is a blocking connect
                // attempt on the main thread. A running HTTP Local session implies the server
                // is up, so record it as reachable: a later manual End Session must not look
                // like a server (re)start (rising edge) on the next probe.
                if (MCPServiceLocator.Bridge.IsRunning)
                {
                    consecutiveUnreachableProbes = 0;
                    SessionState.SetBool(ServerWasReachableKey, true);
                    return;
                }

                bool reachable = MCPServiceLocator.Server.IsLocalHttpServerReachable();
                bool wasReachable = SessionState.GetBool(ServerWasReachableKey, false);
                if (reachable)
                {
                    consecutiveUnreachableProbes = 0;
                    SessionState.SetBool(ServerWasReachableKey, true);
                }
                else if (++consecutiveUnreachableProbes >= UnreachableProbesForServerDown)
                {
                    SessionState.SetBool(ServerWasReachableKey, false);
                }

                if (!reachable)
                {
                    return;
                }

                // A manual End Session is overridden only by the server (re)starting
                // (unreachable -> reachable); otherwise heal any dropped session.
                bool risingEdge = !wasReachable;
                if (risingEdge)
                {
                    SessionState.EraseBool(ManualSessionEndKey);
                }
                else if (SessionState.GetBool(ManualSessionEndKey, false))
                {
                    return;
                }

                connectInProgress = true;
                McpLog.Info(risingEdge
                    ? "Local MCP server became reachable; auto-connecting session."
                    : "Local MCP server reachable but no session running; auto-connecting session.");
                _ = StartSessionAsync();
            }
            catch (Exception ex)
            {
                McpLog.Debug($"Auto-connect poll failed: {ex.Message}");
            }
        }

        private static async System.Threading.Tasks.Task StartSessionAsync()
        {
            try
            {
                bool started = await MCPServiceLocator.Bridge.StartAsync();
                if (started)
                {
                    McpLog.Info("Session connected (external trigger).");
                    MCPForUnityEditorWindow.RequestHealthVerification();
                }
                else
                {
                    autoConnectNotBefore = EditorApplication.timeSinceStartup + FailedStartBackoffSeconds;
                    McpLog.Warn("Connect trigger: failed to start MCP session.");
                }
            }
            catch (Exception ex)
            {
                autoConnectNotBefore = EditorApplication.timeSinceStartup + FailedStartBackoffSeconds;
                McpLog.Warn($"Connect trigger: session start failed: {ex.Message}");
            }
            finally
            {
                connectInProgress = false;
            }
        }

        private static bool MatchesThisProject(string target)
        {
            string projectDir = Path.GetDirectoryName(Application.dataPath);
            string projectName = string.IsNullOrEmpty(projectDir) ? string.Empty : Path.GetFileName(projectDir);
            if (string.Equals(target, projectName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Same hash the stdio status files use (SHA1 of Application.dataPath, first 8 hex chars).
            string hash = Transport.Transports.StdioBridgeHost.ComputeProjectHash(Application.dataPath);
            return target.Length >= 4 && hash.StartsWith(target, StringComparison.OrdinalIgnoreCase);
        }
    }
}
