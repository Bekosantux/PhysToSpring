#if !(PHYSTOSPRING_VRCSDK && PHYSTOSPRING_UNIVRM)
using UnityEditor;
using UnityEngine;

namespace Bekosan.PhysToSpring.DependencyCheck
{
    /// <summary>
    /// 依存パッケージが無いと変換器 (Bekosan.PhysToSpring.Editor) は defineConstraints でコンパイルされず、
    /// メニューも出ないので、その理由をセッションごとに 1 回ログに出す。
    /// </summary>
    [InitializeOnLoad]
    static class DependencyWarning
    {
        const string LoggedKey = "Bekosan.PhysToSpring.DependencyWarningLogged";

        static DependencyWarning()
        {
            if (SessionState.GetBool(LoggedKey, false)) return;
            SessionState.SetBool(LoggedKey, true);
#if !PHYSTOSPRING_VRCSDK
            Debug.LogWarning("[PhysToSpring] VRChat SDK - Avatars (com.vrchat.avatars) が見つからないため無効になっています。");
#endif
#if !PHYSTOSPRING_UNIVRM
            Debug.LogWarning("[PhysToSpring] UniVRM 0.131.0 以上 (com.vrmc.vrm) が見つからないため無効になっています。" +
                             "UniVRM を unitypackage で導入した場合は、Project Settings > Player > Scripting Define Symbols に PHYSTOSPRING_UNIVRM を追加してください。");
#endif
        }
    }
}
#endif
