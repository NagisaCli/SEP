using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Aveva.ApplicationFramework.Presentation;
using SEP.PluginPanel;
using SEP.PluginPanel.Caf;
using SEP.PluginPanel.Model;
using SEP.PluginPanel.UI;
using Keys = System.Windows.Forms.Keys;

internal static class Program
{
    private static int passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            Check("Unprotected checked-state feedback reproduces recursive execution", () =>
            {
                var command = Toggle();
                try { command.Execute(); } catch (InvalidOperationException) { }
                Require(command.Calls == 12, "The fixture must reproduce CAF's checked-state feedback.");
            });
            Check("Protected toggle executes once and retains the checked state", () =>
            {
                var command = Toggle();
                Require(CafCommandRunner.Run(command).Ok && command.Calls == 1 && command.Checked, "Toggle recurred.");
                Require(command.ExecuteOnCheckedChange, "The original setting was not restored.");
                Require(CafCommandRunner.Run(command).Ok && command.Calls == 2 && !command.Checked, "Second toggle failed.");
            });
            Check("Exceptions restore command configuration and remove the reentry handler", () =>
            {
                var command = new ToggleCommand { Fail = true };
                try { CafCommandRunner.Run(command); } catch (InvalidOperationException) { }
                Require(command.ExecuteOnCheckedChange, "Setting leaked after failure.");
                command.Fail = false;
                command.CancelableExecute();
                Require(command.Calls == 2, "Reentry handler leaked after failure.");
            });
            Check("Nested CAF execution is cancelled", () =>
            {
                var command = new ToggleCommand { Reenter = true };
                Require(CafCommandRunner.Run(command).Ok && command.Calls == 1, "Nested execution was not cancelled.");
            });
            Check("Disabled commands do not execute", () =>
            {
                var command = new ToggleCommand { Enabled = false };
                Require(!CafCommandRunner.Run(command).Ok && command.Calls == 0, "Disabled command executed.");
            });
            string previous = Environment.GetEnvironmentVariable("PMLLIB");
            try
            {
                foreach (string value in new[] { @"D:\Plugin\pml", @"D:\Plugin\pml\;D:\Other", @"(""D:/Plugin/pml"" D:\Other)", "D:\\Other\tD:\\Plugin\\pml" })
                {
                    Check("PMLLIB exact path: " + value, () =>
                    {
                        Environment.SetEnvironmentVariable("PMLLIB", value);
                        var card = Card(pml: @"D:\Plugin\pml");
                        StatusProbe.Evaluate(new[] { card });
                        Require(card.State == PluginState.Ready, card.StateDetail);
                    });
                }
                foreach (string value in new[] { @"D:\Plugin\pml-other", @"D:\Plugin\pml\child", @"D:\PrefixD:\Plugin\pml" })
                {
                    Check("PMLLIB rejects partial path: " + value, () =>
                    {
                        Environment.SetEnvironmentVariable("PMLLIB", value);
                        var card = Card(pml: @"D:\Plugin\pml");
                        StatusProbe.Evaluate(new[] { card });
                        Require(card.State == PluginState.NotLoaded, "Partial path was treated as loaded.");
                    });
                }
            }
            finally { Environment.SetEnvironmentVariable("PMLLIB", previous); }
            Check("Registered commands override mismatched package assembly names", () =>
            {
                var card = Card(addin: "Package.Filename", command: "Plugin.Show");
                StatusProbe.Evaluate(new[] { card }, key => true);
                Require(card.State == PluginState.Ready, card.StateDetail);
            });
            Check("A loaded assembly without its command is unavailable", () =>
            {
                var card = Card(addin: "mscorlib", command: "Plugin.Show");
                StatusProbe.Evaluate(new[] { card }, key => false);
                Require(card.State == PluginState.NotLoaded && card.StateDetail.Contains("Commands not registered"), "Missing command was hidden.");
                Require(!card.StateDetail.Contains("It was enabled after"), "An unproven startup cause was asserted.");
            });
            Check("Queued and nested clicks execute only once", () =>
            {
                string testRoot = Path.Combine(Path.GetTempPath(), "sep-check-" + Guid.NewGuid().ToString("N"));
                using (var store = new PanelStore(Path.Combine(testRoot, "manifest.json"), Path.Combine(testRoot, "user.json")))
                {
                    var host = new Host();
                    var vm = new PanelViewModel(store, host, new ShortcutFilter());
                    var card = Card(command: "Plugin.Show");
                    vm.Cards.Add(card);
                    host.OnRun = () => vm.Run(card.Actions[0]);
                    vm.Run(card.Actions[0]);
                    vm.Run(card.Actions[0]);
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    Require(host.Calls == 1, "Clicks queued duplicate execution.");
                }
            });
            Console.WriteLine($"PASS: {passed} checks (CAF API and simulated feedback; native E3D acceptance remains separate).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static ToggleCommand Toggle()
    {
        var command = new ToggleCommand();
        // Attach only the feedback executor: Command.AttachExecutor also requires a running CAF service resolver.
        var field = typeof(Command).GetField("attachedExecutors", BindingFlags.Instance | BindingFlags.NonPublic);
        ((List<CommandExecutor>)field.GetValue(command)).Add(new FeedbackExecutor(command));
        return command;
    }

    private static PluginCard Card(string addin = null, string pml = null, string command = null)
    {
        var card = new PluginCard(new ManifestPlugin { Name = "Test", Addins = addin == null ? null : new List<string> { addin }, PmlPaths = pml == null ? null : new List<string> { pml } });
        if (command != null) card.Actions.Add(new PanelAction(card, new ManifestAction { Id = "Show", Kind = "command", Command = command, Caption = "Show" }, false));
        return card;
    }

    private static void Require(bool ok, string detail) { if (!ok) throw new Exception(detail); }
    private static void Check(string name, Action check) { check(); passed++; Console.WriteLine("PASS " + name); }

    private sealed class ToggleCommand : Command
    {
        public int Calls;
        public bool Fail, Reenter;
        public override void Execute()
        {
            Calls++;
            if (Fail || Calls >= 12) throw new InvalidOperationException("Bounded feedback reproduction.");
            Checked = !Checked;
            if (Reenter) CancelableExecute();
        }
    }

    private sealed class FeedbackExecutor : CommandExecutor
    {
        private readonly Command command;
        private bool value;
        public FeedbackExecutor(Command command) : base(true) { this.command = command; }
        protected override string IdentityDisplay => "Checked-state feedback";
        public override bool Checked
        {
            get => value;
            set { if (this.value == value) return; this.value = value; if (command.ExecuteOnCheckedChange) command.CancelableExecute(); }
        }
        public override bool Enabled { get; set; }
        public override bool Visible { get; set; }
        public override object Value { set { } }
        public override int SelectedIndex { set { } }
        public override ArrayList List { set { } }
        public override void Select() { }
        public override void SetProperty(string property, object value) { }
    }

    private sealed class Host : IPanelHost
    {
        public int Calls;
        public Action OnRun;
        public RunResult Run(string kind, string command) { Calls++; OnRun?.Invoke(); return RunResult.Success(); }
        public bool HasCommand(string key) => true;
        public void TogglePanel(bool focusSearch) { }
        public void ShowPanel() { }
        public void ToggleBar(bool focusSearch) { }
        public IReadOnlyDictionary<Keys, string> E3dShortcuts() => new Dictionary<Keys, string>();
    }
}
