param(
    [string]$ShaderDir = "$PSScriptRoot\..\src\MetroHub\Presentation\Shaders",
    [string]$OutputDir = "$ShaderDir\Bytecode",
    [string]$TargetProfile = "ps_3_0"
)

$ErrorActionPreference = "Stop"

$compilerTypeSource = @"
using System;
using System.Runtime.InteropServices;

public static class D3DCompiler
{
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    public static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPStr)] string srcData,
        UIntPtr srcDataLen,
        [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines,
        IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out ID3DBlob code,
        out ID3DBlob errorMsgs);

    public static byte[] Compile(string hlsl, string sourceName, string entryPoint, string target)
    {
        ID3DBlob code = null;
        ID3DBlob errors = null;
        int hr = D3DCompile(hlsl, (UIntPtr)hlsl.Length, sourceName, IntPtr.Zero, new IntPtr(1), entryPoint, target, 0, 0, out code, out errors);
        if (hr != 0)
        {
            string msg = "Unknown compilation error";
            if (errors != null)
            {
                msg = Marshal.PtrToStringAnsi(errors.GetBufferPointer());
            }
            throw new InvalidOperationException(string.Format("Shader compilation failed (HRESULT: 0x{0:X8}): {1}", hr, msg));
        }
        int len = (int)code.GetBufferSize().ToUInt32();
        byte[] bytes = new byte[len];
        Marshal.Copy(code.GetBufferPointer(), bytes, 0, len);
        return bytes;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'D3DCompiler').Type) {
    Add-Type -TypeDefinition $compilerTypeSource
}

$hlslFiles = Get-ChildItem -Path $ShaderDir -Filter "*.hlsl"
if ($hlslFiles.Count -eq 0) {
    Write-Warning "No .hlsl files found in $ShaderDir"
    exit 0
}

if (-not (Test-Path $OutputDir)) {
    [System.IO.Directory]::CreateDirectory($OutputDir) | Out-Null
}

Push-Location $ShaderDir
try {
    foreach ($file in $hlslFiles) {
        if ($file.Name -like "*Palette*" -or $file.Name -like "*.inc.hlsl") {
            continue
        }
        $outName = [System.IO.Path]::ChangeExtension($file.Name, ".ps")
        $outputPath = [System.IO.Path]::Combine($OutputDir, $outName)
        Write-Host "Compiling $($file.Name) -> $outName [$TargetProfile]..."
        $hlsl = [System.IO.File]::ReadAllText($file.FullName)
        $bytes = [D3DCompiler]::Compile($hlsl, $file.FullName, "main", $TargetProfile)
        [System.IO.File]::WriteAllBytes($outputPath, $bytes)
        Write-Host "Success: $outName ($($bytes.Length) bytes written)." -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
