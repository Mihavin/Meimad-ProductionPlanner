using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Application.MachineAssignments;

/// <summary>
/// A short fingerprint of one Machine's backlog order. The Planning Board sends it with each Machine;
/// a move carries the stamp of the target Machine the planner saw, and the Server refuses the move when
/// another planner or a production event changed that order in the meantime (owner decision
/// 2026-09-27: parallel editing with per-record checks, the Planning Board order checked per Machine).
/// </summary>
internal static class MachineBacklogStamp
{
    internal static string Of(IEnumerable<string> assignmentIdsInOrder)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', assignmentIdsInOrder)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
