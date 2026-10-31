using System;
using System.ComponentModel;
using System.Windows.Forms;
using Aveva.ApplicationFramework.Presentation;

namespace SEP.PluginPanel.Caf
{
    /// <summary>Runs a command without turning its checked-state updates into another tool click.</summary>
    public static class CafCommandRunner
    {
        public static RunResult Run(Command command)
        {
            if (command == null) return RunResult.Failure("Command is not registered in this session.");
            if (!command.Enabled || !command.IsValid) return RunResult.Failure("Command is unavailable in the current E3D context.");

            bool executeOnCheckedChange = command.ExecuteOnCheckedChange;
            Cursor cursor = Cursor.Current;
            bool entered = false;
            CancelEventHandler preventReentry = (sender, e) =>
            {
                if (entered) e.Cancel = true;
                entered = true;
            };
            command.BeforeCommandExecute += preventReentry;
            try
            {
                // CAF StateButtonToolImpl otherwise invokes the command again when the plug-in sets Checked.
                command.ExecuteOnCheckedChange = false;
                command.CancelableExecute();
                return RunResult.Success();
            }
            finally
            {
                command.BeforeCommandExecute -= preventReentry;
                command.ExecuteOnCheckedChange = executeOnCheckedChange;
                Cursor.Current = cursor;
            }
        }
    }
}
