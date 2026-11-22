namespace AccLineChart;

[HarmonyPatch(typeof(TaskStageTarget), "InvokeJudgeEvent")]
internal static class TaskStageTargetInvokeJudgeEventPatch
{
    private static void Postfix(TaskStageTarget __instance, uint result)
    {
        RunRecorder.OnJudge(__instance, result);
    }
}

[HarmonyPatch(typeof(TaskStageTarget), "AddScore")]
internal static class TaskStageTargetAddScorePatch
{
    private static void Postfix(int id, string noteType, bool isAir, float time)
    {
        RunRecorder.OnNoteScored(id, noteType, isAir, time);
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
