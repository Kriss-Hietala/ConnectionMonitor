using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Connection Monitor";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windows = new("ConnectionMonitor");
    private readonly MonitorWindow mainWindow;
    private readonly PluginConfig config;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        mainWindow = new MonitorWindow(config, SaveConfig, chatGui, clientState);
        windows.AddWindow(mainWindow);

        commandManager.AddHandler(
            "/connectionmonitor",
            new CommandInfo((_, _) => mainWindow.Toggle())
            {
                HelpMessage = "Open Connection Monitor."
            });

        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
    }

    private void SaveConfig() => pluginInterface.SavePluginConfig(config);

    public void Dispose()
    {
        mainWindow.Dispose();
        commandManager.RemoveHandler("/connectionmonitor");
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        windows.RemoveAllWindows();
    }

    public sealed class PluginConfig : IPluginConfiguration
    {
        public int Version { get; set; } = 2;
        public string Target { get; set; } = "80.239.145.101";
        public int IntervalMs { get; set; } = 1000;
        public int SampleWindow { get; set; } = 60;
        public bool ResetOnStart { get; set; } = true;
        public bool AutoDetectGameEndpoint { get; set; } = false;
        public List<MonitorHistoryEntry> History { get; set; } = new();
    }

    public sealed class MonitorHistoryEntry
    {
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public string Target { get; set; } = string.Empty;
        public string Source { get; set; } = "Manual";
        public uint TerritoryId { get; set; }
        public int Sent { get; set; }
        public int Received { get; set; }
        public double LossPercent { get; set; }
        public double AverageMs { get; set; }
        public double JitterMs { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public string EndReason { get; set; } = string.Empty;
    }

    public sealed record HopResult(int Ttl, string Address, string Status, long? RoundTripMs);

    public sealed record MonitorSnapshot(
        bool Running,
        int Sent,
        int Received,
        double Current,
        double Average,
        double Jitter,
        double Min,
        double Max,
        List<HopResult> Route,
        bool RouteRunning);

    public sealed class MonitorWindow : Window, IDisposable
    {
        private static readonly HashSet<int> GamePorts = new()
        {
            54992, 54993, 54994, 55006, 55007, 55021
        };

        private readonly PluginConfig config;
        private readonly Action saveConfig;
        private readonly IChatGui chatGui;
        private readonly IClientState clientState;
        private readonly object sync = new();
        private readonly List<double> samples = new();
        private readonly List<HopResult> route = new();

        private CancellationTokenSource? monitorCancellation;
        private Task? monitorTask;
        private bool routeRunning;
        private int monitorGeneration;
        private int routeGeneration;
        private int sent;
        private int received;
        private double current = -1;

        private DateTime sessionStartedAt;
        private string sessionTarget = string.Empty;
        private string sessionSource = "Manual";
        private bool sessionArchived;

        private DateTime nextEndpointScan = DateTime.MinValue;
        private uint lastTerritoryId;
        private string detectedEndpoint = string.Empty;
        private string detectionStatus = "Auto-detection disabled";
        private bool showHistory;

        public MonitorWindow(
            PluginConfig config,
            Action saveConfig,
            IChatGui chatGui,
            IClientState clientState)
            : base("Connection Monitor###ConnectionMonitor")
        {
            this.config = config;
            this.saveConfig = saveConfig;
            this.chatGui = chatGui;
            this.clientState = clientState;
            Size = new Vector2(700, 520);
            SizeCondition = ImGuiCond.FirstUseEver;
            BeginSession("Manual");
        }

        public void Dispose()
        {
            Stop("Plugin unloaded");
        }

        public override void Update()
        {
            if (!config.AutoDetectGameEndpoint)
                return;

            var now = DateTime.UtcNow;
            uint territory = clientState.TerritoryType;
            bool territoryChanged = territory != lastTerritoryId;
            if (!territoryChanged && now < nextEndpointScan)
                return;

            lastTerritoryId = territory;
            nextEndpointScan = now.AddSeconds(5);
            DetectGameEndpoint();
        }

        private void BeginSession(string source)
        {
            sessionStartedAt = DateTime.Now;
            sessionTarget = config.Target;
            sessionSource = source;
            sessionArchived = false;
        }

        private void Start()
        {
            if (monitorTask is { IsCompleted: false })
                return;
            if (string.IsNullOrWhiteSpace(config.Target))
                return;

            if (config.ResetOnStart || sent > 0 || sessionArchived)
                ResetStats(true, "New monitoring session", sessionSource);
            else if (sessionStartedAt == default)
                BeginSession(sessionSource);

            var cancellation = new CancellationTokenSource();
            monitorCancellation = cancellation;
            int generation = ++monitorGeneration;
            string target = config.Target;
            monitorTask = Task.Run(() => MonitorLoopAsync(target, generation, cancellation.Token));
        }

        private void Stop(string reason = "Stopped by user")
        {
            ++monitorGeneration;
            monitorCancellation?.Cancel();
            monitorCancellation = null;
            ArchiveCurrentSession(reason);
        }

        private void ResetStats(bool archiveCurrent, string reason, string nextSource)
        {
            ++monitorGeneration;
            monitorCancellation?.Cancel();
            monitorCancellation = null;

            if (archiveCurrent)
                ArchiveCurrentSession(reason);

            lock (sync)
            {
                sent = 0;
                received = 0;
                current = -1;
                samples.Clear();
                route.Clear();
            }

            ++routeGeneration;
            BeginSession(nextSource);
        }

        private async Task MonitorLoopAsync(string target, int generation, CancellationToken token)
        {
            using var ping = new Ping();
            while (!token.IsCancellationRequested && generation == monitorGeneration)
            {
                try
                {
                    lock (sync)
                    {
                        if (generation != monitorGeneration)
                            break;
                        sent++;
                    }

                    var reply = await ping.SendPingAsync(target, 1500);
                    lock (sync)
                    {
                        if (generation != monitorGeneration || token.IsCancellationRequested)
                            break;

                        if (reply.Status == IPStatus.Success)
                        {
                            received++;
                            current = reply.RoundtripTime;
                            samples.Add(current);
                            while (samples.Count > Math.Clamp(config.SampleWindow, 10, 300))
                                samples.RemoveAt(0);
                        }
                        else
                        {
                            current = -1;
                        }
                    }
                }
                catch
                {
                    lock (sync)
                    {
                        if (generation == monitorGeneration)
                            current = -1;
                    }
                }

                try
                {
                    await Task.Delay(Math.Clamp(config.IntervalMs, 500, 10000), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunRouteAsync()
        {
            int generation;
            string target;
            lock (sync)
            {
                if (routeRunning)
                    return;
                routeRunning = true;
                route.Clear();
                generation = ++routeGeneration;
                target = config.Target;
            }

            try
            {
                using var ping = new Ping();
                var buffer = new byte[32];
                for (int ttl = 1; ttl <= 15; ttl++)
                {
                    var reply = await ping.SendPingAsync(target, 1000, buffer, new PingOptions(ttl, true));
                    string address = reply.Address?.ToString() ?? "?";
                    long? rtt = reply.Status is IPStatus.Success or IPStatus.TtlExpired
                        ? reply.RoundtripTime
                        : null;

                    lock (sync)
                    {
                        if (generation != routeGeneration)
                            return;
                        route.Add(new HopResult(ttl, address, reply.Status.ToString(), rtt));
                    }

                    if (reply.Status == IPStatus.Success)
                        break;
                }
            }
            catch (Exception ex)
            {
                chatGui.Print($"[Connection Monitor] Route test failed: {ex.Message}");
            }
            finally
            {
                lock (sync)
                {
                    if (generation == routeGeneration)
                        routeRunning = false;
                }
            }
        }

        private void ArchiveCurrentSession(string reason)
        {
            if (sessionArchived || sessionStartedAt == default)
                return;

            int sentCopy;
            int receivedCopy;
            List<double> data;
            lock (sync)
            {
                sentCopy = sent;
                receivedCopy = received;
                data = samples.ToList();
            }

            if (sentCopy == 0)
                return;

            double average = data.Count == 0 ? 0 : data.Average();
            double jitter = data.Count < 2
                ? 0
                : Math.Sqrt(data.Average(x => (x - average) * (x - average)));

            config.History.Add(new MonitorHistoryEntry
            {
                StartedAt = sessionStartedAt,
                EndedAt = DateTime.Now,
                Target = sessionTarget,
                Source = sessionSource,
                TerritoryId = clientState.TerritoryType,
                Sent = sentCopy,
                Received = receivedCopy,
                LossPercent = 100d * (sentCopy - receivedCopy) / sentCopy,
                AverageMs = average,
                JitterMs = jitter,
                MinMs = data.Count == 0 ? 0 : data.Min(),
                MaxMs = data.Count == 0 ? 0 : data.Max(),
                EndReason = reason
            });

            const int maxHistoryEntries = 200;
            if (config.History.Count > maxHistoryEntries)
                config.History.RemoveRange(0, config.History.Count - maxHistoryEntries);

            sessionArchived = true;
            saveConfig();
        }

        private void DetectGameEndpoint()
        {
            try
            {
                var candidates = IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpConnections()
                    .Where(c => c.State == TcpState.Established)
                    .Where(c => c.RemoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(c => IsPublicAddress(c.RemoteEndPoint.Address))
                    .Where(c => GamePorts.Contains(c.RemoteEndPoint.Port) || IsKnownSquareEnixRange(c.RemoteEndPoint.Address))
                    .OrderByDescending(c => GamePorts.Contains(c.RemoteEndPoint.Port))
                    .ThenByDescending(c => IsKnownSquareEnixRange(c.RemoteEndPoint.Address))
                    .ThenBy(c => c.RemoteEndPoint.Address.ToString())
                    .ToList();

                if (candidates.Count == 0)
                {
                    detectionStatus = "No matching established FFXIV endpoint was found";
                    return;
                }

                string endpoint = candidates[0].RemoteEndPoint.Address.ToString();
                detectedEndpoint = endpoint;
                detectionStatus = $"Detected {endpoint}:{candidates[0].RemoteEndPoint.Port}";

                if (!string.Equals(endpoint, config.Target, StringComparison.OrdinalIgnoreCase))
                {
                    bool restart = monitorTask is { IsCompleted: false };
                    ResetStats(true, "Endpoint changed", "Auto-detected endpoint");
                    config.Target = endpoint;
                    saveConfig();
                    chatGui.Print($"[Connection Monitor] Detected game endpoint: {endpoint}. Statistics were reset.");

                    if (restart)
                        Start();
                }
            }
            catch (Exception ex)
            {
                detectionStatus = $"Auto-detection unavailable: {ex.GetType().Name}";
            }
        }

        private static bool IsKnownSquareEnixRange(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 80 && bytes[1] == 239;
        }

        private static bool IsPublicAddress(IPAddress address)
        {
            var b = address.GetAddressBytes();
            if (b.Length != 4)
                return false;

            if (b[0] == 10 || b[0] == 127 || b[0] == 0)
                return false;
            if (b[0] == 169 && b[1] == 254)
                return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                return false;
            if (b[0] == 192 && b[1] == 168)
                return false;

            return true;
        }

        private MonitorSnapshot Snapshot()
        {
            lock (sync)
            {
                var data = samples.ToList();
                double average = data.Count == 0 ? 0 : data.Average();
                double jitter = data.Count < 2
                    ? 0
                    : Math.Sqrt(data.Average(x => (x - average) * (x - average)));

                return new MonitorSnapshot(
                    monitorTask is { IsCompleted: false },
                    sent,
                    received,
                    current,
                    average,
                    jitter,
                    data.Count == 0 ? 0 : data.Min(),
                    data.Count == 0 ? 0 : data.Max(),
                    route.ToList(),
                    routeRunning);
            }
        }

        private static Vector4 MetricColor(double loss, double jitter)
            => loss > 5 || jitter > 20 ? new Vector4(1f, .25f, .25f, 1f)
             : loss > 1 || jitter > 8 ? new Vector4(1f, .8f, .2f, 1f)
             : new Vector4(.25f, 1f, .45f, 1f);

        public override void Draw()
        {
            string target = config.Target;
            ImGui.SetNextItemWidth(220);
            bool targetEdited = ImGui.InputText("Target (IP or hostname)", ref target, 255);
            if (targetEdited)
                config.Target = target.Trim();

            ImGui.SameLine();
            if (ImGui.Button("Save target"))
            {
                string newTarget = config.Target.Trim();
                if (!string.Equals(newTarget, sessionTarget, StringComparison.OrdinalIgnoreCase))
                {
                    bool restart = monitorTask is { IsCompleted: false };
                    ResetStats(true, "Manual target changed", "Manual");
                    config.Target = newTarget;
                    if (restart)
                        Start();
                }
                saveConfig();
            }

            int interval = config.IntervalMs;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderInt("Interval (ms)", ref interval, 500, 5000))
            {
                config.IntervalMs = interval;
                saveConfig();
            }

            ImGui.SameLine();
            int window = config.SampleWindow;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderInt("Sample window", ref window, 10, 300))
            {
                config.SampleWindow = window;
                saveConfig();
            }

            bool resetOnStart = config.ResetOnStart;
            if (ImGui.Checkbox("Reset statistics when monitoring starts", ref resetOnStart))
            {
                config.ResetOnStart = resetOnStart;
                saveConfig();
            }

            bool autoDetect = config.AutoDetectGameEndpoint;
            if (ImGui.Checkbox("Auto-detect active game endpoint (experimental)", ref autoDetect))
            {
                config.AutoDetectGameEndpoint = autoDetect;
                detectionStatus = autoDetect ? "Waiting for the next scan" : "Auto-detection disabled";
                saveConfig();
            }

            ImGui.SameLine();
            if (ImGui.Button("Detect now"))
                DetectGameEndpoint();

            ImGui.TextDisabled(detectionStatus);
            if (!string.IsNullOrEmpty(detectedEndpoint))
                ImGui.TextDisabled($"Last detected endpoint: {detectedEndpoint}");

            var s = Snapshot();
            ImGui.Separator();
            if (!s.Running)
            {
                if (ImGui.Button("Start monitoring"))
                    Start();
            }
            else if (ImGui.Button("Stop monitoring"))
            {
                Stop();
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset stats"))
                ResetStats(true, "Manual reset", sessionSource);

            ImGui.SameLine();
            if (ImGui.Button(s.RouteRunning ? "Route test running..." : "Run route test") && !s.RouteRunning)
                _ = Task.Run(RunRouteAsync);

            ImGui.SameLine();
            if (ImGui.Button(showHistory ? "Hide history" : "History"))
                showHistory = !showHistory;

            double loss = s.Sent == 0 ? 0 : 100d * (s.Sent - s.Received) / s.Sent;
            var color = MetricColor(loss, s.Jitter);
            ImGui.Spacing();
            ImGui.TextColored(s.Running ? color : new Vector4(.7f, .7f, .7f, 1f), s.Running ? "● Monitoring" : "● Stopped");
            ImGui.Text($"Session target: {sessionTarget} ({sessionSource})");
            ImGui.Text($"Sent / received: {s.Sent} / {s.Received}");

            if (s.Sent == 0)
            {
                ImGui.TextDisabled("Packet loss: — (no samples)");
                ImGui.TextDisabled("Current ping: —");
                ImGui.TextDisabled("Average: —  Range: —  Jitter: —");
            }
            else
            {
                ImGui.TextColored(color, $"Packet loss: {loss:F1}%");
                ImGui.Text($"Current ping: {(s.Current < 0 ? "timeout" : $"{s.Current:F0} ms")}");
                ImGui.Text($"Average: {s.Average:F1} ms  Range: {s.Min:F1}–{s.Max:F1} ms  Jitter: {s.Jitter:F1} ms");
            }

            if (showHistory)
                DrawHistory();

            ImGui.Separator();
            ImGui.Text("Manual route test (ICMP / TTL; intermediate hops are often filtered):");
            if (ImGui.BeginTable("Route", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Hop", ImGuiTableColumnFlags.WidthFixed, 45);
                ImGui.TableSetupColumn("Address");
                ImGui.TableSetupColumn("Status");
                ImGui.TableSetupColumn("RTT", ImGuiTableColumnFlags.WidthFixed, 75);
                ImGui.TableHeadersRow();

                foreach (var hop in s.Route)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text(hop.Ttl.ToString());
                    ImGui.TableNextColumn(); ImGui.Text(hop.Address);
                    ImGui.TableNextColumn(); ImGui.Text(hop.Status);
                    ImGui.TableNextColumn(); ImGui.Text(hop.RoundTripMs is null ? "—" : $"{hop.RoundTripMs} ms");
                }

                ImGui.EndTable();
            }
        }

        private void DrawHistory()
        {
            ImGui.Separator();
            ImGui.Text($"Saved sessions: {config.History.Count} (maximum 200)");
            ImGui.SameLine();
            if (ImGui.Button("Clear saved history"))
            {
                config.History.Clear();
                saveConfig();
            }

            if (ImGui.BeginTable("MonitorHistory", 7,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit,
                    new Vector2(0, 180)))
            {
                ImGui.TableSetupColumn("Started");
                ImGui.TableSetupColumn("Target");
                ImGui.TableSetupColumn("Source");
                ImGui.TableSetupColumn("Sent/Recv");
                ImGui.TableSetupColumn("Loss");
                ImGui.TableSetupColumn("Average");
                ImGui.TableSetupColumn("Reason");
                ImGui.TableHeadersRow();

                foreach (var entry in config.History.OrderByDescending(x => x.EndedAt).Take(50))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text(entry.StartedAt.ToString("MM-dd HH:mm:ss"));
                    ImGui.TableNextColumn(); ImGui.Text(entry.Target);
                    ImGui.TableNextColumn(); ImGui.Text(entry.Source);
                    ImGui.TableNextColumn(); ImGui.Text($"{entry.Sent}/{entry.Received}");
                    ImGui.TableNextColumn(); ImGui.Text($"{entry.LossPercent:F1}%");
                    ImGui.TableNextColumn(); ImGui.Text(entry.Received == 0 ? "—" : $"{entry.AverageMs:F1} ms");
                    ImGui.TableNextColumn(); ImGui.Text(entry.EndReason);
                }

                ImGui.EndTable();
            }
        }
    }
}
