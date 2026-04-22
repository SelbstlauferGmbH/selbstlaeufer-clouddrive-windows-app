using System;

namespace CloudDrive.Core.Helpers
{
    public static class DebugHelper
    {
        /// <summary>
        /// Forces an immediate process termination without running any standard
        /// unhandled exception handlers or writing crash logs.
        /// </summary>
        public static void TriggerSilentCrash()
        {
            Environment.FailFast("Requested silent crash via DebugHelper.");
        }
    }
}