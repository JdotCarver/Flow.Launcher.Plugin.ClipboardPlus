// WindowsImportPinPolicy.cs
using System.Collections.Generic;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Protects imported records when Windows pin metadata cannot be verified.</summary>
public static class WindowsImportPinPolicy
{
    public static bool ProtectUnverified(IList<ClipboardData> records, bool pinStateTrusted, bool databaseAvailable)
    {
        if (pinStateTrusted || records.Count == 0) return false;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            record.Pinned = true;
            // Windows-only mode has no plugin database. In database mode the
            // safety pin must survive a restart even when retention is disabled.
            if (databaseAvailable) record.Saved = true;
            records[index] = record;
        }
        return true;
    }
}
