using System;

namespace WUGrid.Core
{
    /// <summary>
    /// Where a server is in the round. Before its restart (booted before the cut-off) the state says whether a restart is
    /// pending — what the restart is for; a server with nothing pending can still be restarted and only counts as done once
    /// it is BACK.
    /// </summary>
    public enum ServerState { Checking, RestartPending, NoRestartPending, Installing, Down, Booting, Back }

    /// <summary>Why a BACK server is not clean; several can apply at once.</summary>
    [Flags]
    public enum BackFlag
    {
        None = 0,
        RebootPending = 1,

        /// <summary>An update's last result since the cut-off is a failure (Windows Update event 20).</summary>
        UpdateFailed = 2,

        /// <summary>A service that ran before the restart (or is Automatic) is not running.</summary>
        ServiceStopped = 4,
    }

    /// <summary>Routine work a clean BACK server may still be doing: shown as a note, never flagged.</summary>
    public enum BackActivity { None, ServicingFinishing, ComponentCleanup }

    /// <summary>What one server's <see cref="Observation"/> means for the round.</summary>
    public sealed class ServerStatus
    {
        public ServerStatus(ServerState state, BackFlag flag = BackFlag.None, BackActivity activity = BackActivity.None,
                            TimeSpan? elapsed = null, string note = "", bool likelyTransient = false)
        {
            State = state;
            Flag = flag;
            Activity = activity;
            Elapsed = elapsed;
            Note = note ?? "";
            LikelyTransient = likelyTransient;
        }

        public ServerState State { get; }
        public BackFlag Flag { get; }
        public BackActivity Activity { get; }

        /// <summary>Time down (DOWN) or since boot (BACK); null for the other states.</summary>
        public TimeSpan? Elapsed { get; }

        /// <summary>Human-readable detail for the grid.</summary>
        public string Note { get; }

        /// <summary>The note carries an asterisk: an error that is usually temporary while a server restarts
        /// (<see cref="StateClassifier.TransientFootnote"/>).</summary>
        public bool LikelyTransient { get; }

        public bool IsClean => State == ServerState.Back && Flag == BackFlag.None;

        /// <summary>For the State column: the state, with "· PENDING" when a BACK server has a restart pending again.</summary>
        public string Label => State == ServerState.Back && Flag.HasFlag(BackFlag.RebootPending) ? "BACK · PENDING" : State.ToLabel();
    }

    public static class ServerStateExtensions
    {
        public static string ToLabel(this ServerState state)
        {
            switch (state)
            {
                case ServerState.Checking: return "checking...";
                case ServerState.RestartPending: return "RESTART PENDING";
                case ServerState.NoRestartPending: return "NO RESTART PENDING";
                case ServerState.Installing: return "INSTALLING";
                case ServerState.Down: return "DOWN";
                case ServerState.Booting: return "BOOTING";
                case ServerState.Back: return "BACK";
                default: return state.ToString();
            }
        }
    }
}
