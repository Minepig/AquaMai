using AquaMai.Config.Attributes;
using HarmonyLib;
using Manager;
using System;
using System.Collections.Generic;
using MelonLoader;

namespace AquaMai.Mods.GameSystem;

[ConfigSection(
    zh: "音频独占与八声道设置",
    en: "Audio Exclusive and 8-Channel Settings")]
public static class Sound
{
    [ConfigEntry(
        zh: "是否启用音频独占",
        en: "Enable Audio Exclusive")]
    private readonly static bool enableExclusive = false;

    [ConfigEntry(
        zh: "是否启用八声道",
        en: "Enable 8-Channel")]
    private readonly static bool enable8Channel = false;
    
    [ConfigEntry(
        zh: """
            将1P耳机音量同步给游戏主音量（外放音量）
            注意：Maimai处理耳机音量的方式未知，若此功能与八声道同时开启，可能导致耳机音量被缩放两次，推荐在仅使用1P且不开启八声道时使用,
            """,
        en: """
            Sync 1P headphone volume to game master volume
            Maimai's headphone volume handling is unclear. Enabling with 8ch may double-scale headphone volume. It is recommended to use this only in 1P without 8ch.
            """)]
    private readonly static bool sync1pVolumeToMaster = false;

    [ConfigEntry(
        en: "Music Volume.",
        zh: "乐曲音量")]
    private readonly static float musicVolume = 1.0f;

    private static CriAtomUserExtension.AudioClientShareMode AudioShareMode => enableExclusive ? CriAtomUserExtension.AudioClientShareMode.Exclusive : CriAtomUserExtension.AudioClientShareMode.Shared;

    private const ushort wBitsPerSample = 32;
    private const uint nSamplesPerSec = 48000u;
    private static ushort nChannels => enable8Channel ? (ushort)8 : (ushort)2;
    private static ushort nBlockAlign => (ushort)(wBitsPerSample / 8 * nChannels);
    private static uint nAvgBytesPerSec => nSamplesPerSec * nBlockAlign;

    private static CriAtomUserExtension.WaveFormatExtensible CreateFormat() =>
        new()
        {
            Format = new CriAtomUserExtension.WaveFormatEx
            {
                wFormatTag = 65534,
                nSamplesPerSec = nSamplesPerSec,
                wBitsPerSample = wBitsPerSample,
                cbSize = 22,
                nChannels = nChannels,
                nBlockAlign = nBlockAlign,
                nAvgBytesPerSec = nAvgBytesPerSec
            },
            Samples = new CriAtomUserExtension.Samples
            {
                wValidBitsPerSample = 24,
            },
            dwChannelMask = enable8Channel ? 1599u : 3u,
            SubFormat = new Guid("00000001-0000-0010-8000-00aa00389b71")
        };

    [HarmonyPrefix]
    // Original typo
    [HarmonyPatch(typeof(WasapiExclusive), "Intialize")]
    public static bool InitializePrefix()
    {
        CriAtomUserExtension.SetAudioClientShareMode(AudioShareMode);
        CriAtomUserExtension.SetAudioBufferTime(160000uL);
        var format = CreateFormat();
        CriAtomUserExtension.SetAudioClientFormat(ref format);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SoundManager), "Play")]
    public static void PlayPrefix(SoundManager.AcbID acbID,
        SoundManager.PlayerID playerID,
        int cueID,
        bool prepare,
        int target,
        int startTime,
        ref float volume)
    {
        if (acbID == SoundManager.AcbID.Music && playerID == SoundManager.PlayerID.Music)
        {
            volume = musicVolume;
        }
    }
    
    /*
     * 将1P耳机音量同步给游戏主音量（外放音量）
     * Sync 1P headphone volume to game master volume
     *
     * 注意：Maimai处理耳机音量的方式未知，若此功能与八声道同时开启，可能导致耳机音量被缩放两次，
     *       推荐在仅使用1P且不开启八声道时使用
     * Note: Maimai's headphone volume handling is unclear. Enabling with 8ch may double-scale headphone volume.
     *       It is recommended to use this only in 1P without 8ch.
     */
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SoundCtrl), nameof(SoundCtrl.Initialize))]
    public static void SoundCtrl_Initialize_Prefix(SoundCtrl __instance, SoundCtrl.InitParam param)
    {
        __instance._masterVolume = 0.05f; // Default headphone volume
        // MelonLogger.Msg("master volume initialized to 0.05");
        
        // Initialization
        _playerVolumes = new float[param.PlayerNum];
        for (var i = 0; i < param.PlayerNum; i++)
        {
            _playerVolumes[i] = 1f;
        }
    }

    private static float[] _playerVolumes;
    
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SoundCtrl), nameof(SoundCtrl.SetMasterVolume))]
    public static void SoundCtrl_SetMasterVolume_Postfix(SoundCtrl __instance, float[] ____headPhoneVolume, float volume)
    {
        __instance._masterVolume = __instance.Adjust0_1(volume * ____headPhoneVolume[0]);
        // MelonLogger.Msg($"master volume set to {__instance._masterVolume}");
    }
    
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SoundCtrl), nameof(SoundCtrl.ResetMasterVolume))]
    public static void SoundCtrl_ResetMasterVolume_Postfix(SoundCtrl __instance, float[] ____headPhoneVolume)
    {
        __instance._masterVolume = __instance.Adjust0_1(____headPhoneVolume[0]);
        // MelonLogger.Msg($"master volume reset to {__instance._masterVolume}");
    }
    
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SoundCtrl), nameof(SoundCtrl.SetHeadPhoneVolume))]
    public static void SoundCtrl_SetHeadPhoneVolume_Postfix(SoundCtrl __instance, int targerID, float volume)
    {
        // MelonLogger.Msg($"setting headphone volume : target {targerID}, volume {volume}");
        if (targerID != 0) return;
        
        __instance._masterVolume = __instance.Adjust0_1(volume);
        // MelonLogger.Msg($"master volume set to {__instance._masterVolume}");

        MelonLogger.Msg("Syncing volume");
        var dictTraverse = Traverse.Create(__instance).Field<Dictionary<int, object>>("_players");
        MelonLogger.Msg("_players get: " + dictTraverse.ToString());
        foreach (var pair in dictTraverse.Value)
        {
            var player = pair.Value; // SoundCtrl.PlayerObj
            var key = pair.Key;
            var newVolume = __instance.Adjust0_1(__instance._masterVolume * _playerVolumes[key]);
            
            var trav = Traverse.Create(player);
            MelonLogger.Msg($"player #{key} get: {trav.ToString()}");
            if (!trav.Method("IsReady").GetValue<bool>()) continue;

            MelonLogger.Msg($"#{key} checking target");
            switch (trav.Field<int>("TargetID").Value)
            {
                case 0:
                    MelonLogger.Msg("case 0");
                    trav.Method("SetAisac").GetValue(4, newVolume);
                    break;
                case 1:
                    MelonLogger.Msg("case 1");
                    trav.Method("SetAisac").GetValue(5, newVolume);
                    break;
                case 2:
                    MelonLogger.Msg("case 2");
                    trav.Method("SetAisac").GetValue(4, newVolume);
                    trav.Method("SetAisac").GetValue(5, newVolume);
                    break;
            }
            MelonLogger.Msg($"#{key} SetAisac finished");
            trav.Field<bool>("NeedUpdate").Value = true;
            MelonLogger.Msg($"player #{key} synced");
        }
        
        // The code above is supposed to do following things,
        // but SoundCtrl.PlayerObj is private so we need this shit
        // ================================================================================
        // foreach (KeyValuePair<int, SoundCtrl.PlayerObj> player in this._players)
        // {
        //     if (player.Value.IsReady())
        //     {
        //         switch (player.Value.TargetID)
        //         {
        //             case 0:
        //                 player.Value.SetAisac(4, volume);
        //                 break;
        //             case 1:
        //                 player.Value.SetAisac(5, volume);
        //                 break;
        //             case 2:
        //                 player.Value.SetAisac(4, volume);
        //                 player.Value.SetAisac(5, volume);
        //                 break;
        //         }
        //         player.Value.NeedUpdate = true;
        //     }
        // }
        // ================================================================================
    }
    
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SoundCtrl), nameof(SoundCtrl.Play))]
    public static void SoundCtrl_Play_Prefix(SoundCtrl __instance, SoundCtrl.PlaySetting setting)
    {
        _playerVolumes[setting.PlayerID] = (setting.Volume < 0f) ? 1.0f : setting.Volume;
        if (setting.PlayerID != 3 && setting.PlayerID != 4 && setting.PlayerID != 5)
        {
            return;
        }
        // PlayerID 3 ~ 5 are not controlled by master volume, so we need a extra scaling
        // MelonLogger.Msg($"scaling volume: {__instance._masterVolume}");
        setting.Volume *= __instance._masterVolume;
    }
    
    
}
