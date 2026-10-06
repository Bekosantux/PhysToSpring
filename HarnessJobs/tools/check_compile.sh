#!/bin/sh
# Unity を使わずにハーネスと変換ライブラリの Runtime / Editor を Roslyn でコンパイルチェックする。
cd "$(dirname "$0")/../.." || exit 1
E="C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data"; SA=Library/ScriptAssemblies
H=Assets/Bekosan/PhysToSpringHarness
set -- "-r:$E/NetStandard/ref/2.1.0/netstandard.dll"
for f in "$E"/NetStandard/compat/2.1.0/shims/netfx/mscorlib.dll "$E"/NetStandard/compat/2.1.0/shims/netfx/System.dll "$E"/NetStandard/compat/2.1.0/shims/netfx/System.Core.dll "$E"/Managed/UnityEngine/UnityEngine*.dll \
    $SA/SpringBoneJobs.dll $SA/UniGLTF.dll $SA/UniGLTF.Utils.dll $SA/Unity.Mathematics.dll $SA/VRM10.dll \
    $(find Library/PackageCache Packages -name Newtonsoft.Json.dll | head -1) \
    $(find Packages Library/PackageCache \( -name VRC.Dynamics.dll -o -name VRC.SDK3.Dynamics.PhysBone.dll \) | sort -u); do
  set -- "$@" "-r:$f"
done
OUT="${TEMP:-/tmp}/p2s_check"; mkdir -p "$OUT"
L=Assets/Bekosan/PhysToSpring
dotnet "$E/DotNetSdkRoslyn/csc.dll" -nologo -nostdlib+ -nowarn:1701,1702 -t:library -out:"$OUT/Bekosan.PhysToSpring.dll" "$@" $L/Runtime/*.cs || exit 1
dotnet "$E/DotNetSdkRoslyn/csc.dll" -nologo -nostdlib+ -nowarn:1701,1702 -t:library -out:"$OUT/Bekosan.PhysToSpring.Editor.dll" "$@" "-r:$E/Managed/UnityEditor.dll" "-r:$SA/UniHumanoid.dll" "-r:$OUT/Bekosan.PhysToSpring.dll" $L/Editor/*.cs || exit 1
dotnet "$E/DotNetSdkRoslyn/csc.dll" -nologo -nostdlib+ -nowarn:1701,1702 -t:library -out:"$OUT/Runtime.dll" "$@" $H/Runtime/*.cs || exit 1
dotnet "$E/DotNetSdkRoslyn/csc.dll" -nologo -nostdlib+ -nowarn:1701,1702 -t:library -out:"$OUT/Editor.dll" "$@" "-r:$E/Managed/UnityEditor.dll" "-r:$OUT/Runtime.dll" "-r:$OUT/Bekosan.PhysToSpring.dll" "-r:$OUT/Bekosan.PhysToSpring.Editor.dll" $H/Editor/*.cs || exit 1
echo "compile OK"
