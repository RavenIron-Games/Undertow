using HarmonyLib;
using RavenIron.Undertow.Net;

namespace RavenIron.Undertow.Patches
{
    /// <summary>
    /// The server checks who really sent a config message before anything reads it.
    ///
    /// Valheim's routed RPC carries a sender id that the sending machine writes itself, and in
    /// 1.0.15 <c>ZRoutedRpc.RPC_RoutedRPC</c> hands it to the handler (and relays it onward)
    /// without comparing it to the connection the packet came in on. Every trust decision the
    /// config sync makes — "is this push from an admin", "is this payload from the server" —
    /// rests on that id, so this prefix binds it: on the server, for Undertow's own four
    /// messages only, a packet whose claimed sender is not the peer on that connection is
    /// dropped before it is handled or relayed. See <see cref="ConfigSync.AllowRoutedPacket"/>.
    ///
    /// SERVER-ONLY IN EFFECT, and wire-compatible: nothing about what a client sends changes, and
    /// every other mod's routed traffic passes through untouched. The method is private in the
    /// shipping assembly, so it is named by string; the parameters (<c>ZRpc</c>, <c>ZPackage</c>)
    /// are public types. House rule 1: a behaviour prefix at low priority that honours a skip
    /// another mod already asked for.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    public static class Patch_RoutedRpc_Sender
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(ZRpc rpc, ZPackage pkg, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            return ConfigSync.AllowRoutedPacket(rpc, pkg);
        }
    }
}
