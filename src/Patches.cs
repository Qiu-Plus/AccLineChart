namespace AccLineChart;

/// <summary>Per-note callbacks (the same two hooks Info+ uses) drive miss/marker detection.</summary>
[HarmonyPatch(typeof(BattleEnemyManager), "SetPlayResult")]
internal static class BattleEnemyManagerSetPlayResultPatch
{
    private static void Postfix(int idx, byte result)
    {
        RunRecorder.OnNoteResult(idx, result);
    }
}

[HarmonyPatch(typeof(Il2CppGameLogic.GameMissPlay), "MissCube")]
internal static class GameMissPlayMissCubePatch
{
    private static void Postfix(int idx)
    {
        RunRecorder.OnMissCube(idx);
    }
}

[HarmonyPatch(typeof(Il2Cpp.PnlVictory), "OnVictory", new[]
{
    typeof(Il2CppSystem.Object),
    typeof(Il2CppSystem.Object),
    typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>),
})]
internal static class PnlVictoryOnVictoryPatch
{
    private static void Postfix(Il2Cpp.PnlVictory __instance)
    {
        RunRecorder.FinalizeResults();
        ChartWindow.ShowResults();
    }
}

[HarmonyPatch(typeof(StageBattleComponent), "GameStart")]
internal static class StageBattleComponentGameStartPatch
{
    private static void Postfix()
    {
        RunRecorder.OnRunStarted();
    }
}
