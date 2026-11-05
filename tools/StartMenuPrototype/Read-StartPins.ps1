# Read-only prototype. Cryptographic code adapted from Start2Tool (MIT); see LICENSE-Start2Tool.txt.
[CmdletBinding()]
param([string]$InputPath = (Join-Path $env:LOCALAPPDATA "Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState\start2.bin"), [string]$OutputPath)
$ErrorActionPreference = "Stop"
$Script:MAGIC_GUID = [byte[]](0xE2,0x7A,0xE1,0x4B,0x01,0xFC,0x4D,0x1B,0x9C,0x00,0x81,0x0B,0xDE,0x6E,0x51,0x85)
$Script:HEADER_CONST = [byte[]](0x4E,0x5A,0x5F,0x47,0x00,0x5B,0xB1,0x49,0x8A,0x5C,0x92,0xAF,0x90,0x84,0xF9,0x5E)
$Script:PROV_KEY_DW0=0x3B21D91EL; $Script:PAD_SEED_CONST=0x00AF4D97L; $Script:MIN_KEY_LEN=0x40; $Script:MAX_KEY_LEN=0x80; $Script:IV_LEN=0x10; $Script:PAD_TOTAL=0x200
class MT19937 {
  [uint32[]]$State
  [int]$Index

  MT19937([uint32]$seed) {
    $this.State = New-Object 'uint32[]' 624
    $this.State[0] = $seed
    for ($i = 1; $i -lt 624; $i++) {
      [long]$prev = $this.State[$i - 1]
      [long]$x = $prev -bxor ($prev -shr 30)
      $x = ($x * 0x6C078965L + $i) -band 0xFFFFFFFFL
      $this.State[$i] = [uint32]$x
    }
    $this.Index = 624
  }

  [void]Refresh() {
    for ($i = 0; $i -lt 624; $i++) {
      [long]$y = ([long]$this.State[$i] -band 0x80000000L) -bor `
        ([long]$this.State[($i + 1) % 624] -band 0x7FFFFFFFL)
      [long]$val = [long]$this.State[($i + 397) % 624] -bxor ($y -shr 1)
      if ($y -band 1L) { $val = $val -bxor 0x9908B0DFL }
      $this.State[$i] = [uint32]($val -band 0xFFFFFFFFL)
    }
    $this.Index = 0
  }

  [uint32]Next() {
    if ($this.Index -ge 624) { $this.Refresh() }
    [long]$y = [long]$this.State[$this.Index]
    $this.Index++
    $y = ($y -bxor ($y -shr 11)) -band 0xFFFFFFFFL
    $y = ($y -bxor (($y -shl 7)  -band 0x9D2C5680L)) -band 0xFFFFFFFFL
    $y = ($y -bxor (($y -shl 15) -band 0xEFC60000L)) -band 0xFFFFFFFFL
    $y = ($y -bxor ($y -shr 18)) -band 0xFFFFFFFFL
    return [uint32]$y
  }

  [void]Discard([int]$n) {
    for ($i = 0; $i -lt $n; $i++) { [void]$this.Next() }
  }
}

########################################
# KDF
########################################
function Script:Get-UniformUint32 {
  param(
    [Parameter(Mandatory)] [MT19937]$Mt,
    [Parameter(Mandatory)] [uint32]$Range   # range_size = max - min
  )
  if ($Range -eq 0) { return [uint32]0 }
  if ($Range -eq 0xFFFFFFFFL) { return $Mt.Next() }
  [long]$bound = [long]$Range + 1L
  while ($true) {
    [long]$v = [long]$Mt.Next()
    [long]$rem = $v % $bound
    $condA = ([math]::Floor([long]0xFFFFFFFFL / $bound)) -le ([math]::Floor($v / $bound))
    $condB = ([long]0xFFFFFFFFL % $bound) -ne $Range
    if (-not ($condA -and $condB)) { return [uint32]$rem }
  }
  return [uint32]0   # unreachable; placates parser
}

function Script:Get-AlphaNumericKey {
  param(
    [Parameter(Mandatory)] [uint32]$Seed,
    [Parameter(Mandatory)] [int]$Length
  )
  $mt = [MT19937]::new($Seed)
  [long]$v = 0

  # Phase 1: discard pass
  while ($true) {
    $v = [long]$mt.Next()
    if ([long]([math]::Floor($v / 0x3E9L)) -le 0x417873L) { break }
  }
  $mt.Discard([int]($v % 0x3E9L))

  # Phase 2: emit `Length` bytes in [0x20, 0x80)
  $out = New-Object 'byte[]' $Length
  for ($i = 0; $i -lt $Length; $i++) {
    [long]$w =0
    while ($true) {
      $w = [long]$mt.Next()
      if ([long]([math]::Floor($w / 0x60L)) -le 0x2AAAAA9L) { break }
    }
    $out[$i] = [byte](($w % 0x60L) + 0x20L)
  }
  return , $out
}

function Script:Get-SymmetricKey {
  param([Parameter(Mandatory)] [uint32]$SymSeed)

  $mt = [MT19937]::new($SymSeed)
  $keySeed = $mt.Next()
  $randOff = Get-UniformUint32 -Mt $mt -Range ([uint32]($Script:MAX_KEY_LEN - $Script:MIN_KEY_LEN))
  $ivSeed = $mt.Next()

  $keyLen = [int]$randOff + $Script:MIN_KEY_LEN
  $keyBytes = Get-AlphaNumericKey -Seed $keySeed -Length $keyLen
  $ivBytes = Get-AlphaNumericKey -Seed $ivSeed  -Length $Script:IV_LEN

  # WinRT picks the largest AES variant supported, so the first 32 bytes are
  # used as an AES-256 key. (key_str is 64-128 bytes, IV is always 16.)
  $keyTrunc = New-Object 'byte[]' 32
  [Array]::Copy($keyBytes, 0, $keyTrunc, 0, 32)

  return [PSCustomObject]@{
    Key     = $keyTrunc
    FullKey = $keyBytes
    Iv      = $ivBytes
    KeyLen  = $keyLen
  }
}

########################################
# AES-CBC-PKCS7
########################################
function Script:Invoke-AesCbcPkcs7 {
  param(
    [Parameter(Mandatory)] [byte[]]$Data,
    [Parameter(Mandatory)] [byte[]]$Key,
    [Parameter(Mandatory)] [byte[]]$InitVector,
    [Parameter(Mandatory)] [ValidateSet('Encrypt', 'Decrypt')] [string]$Mode
  )
  $aes = [System.Security.Cryptography.Aes]::Create()
  try {
    $aes.Mode    = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key     = $Key
    $aes.IV      = $InitVector
    if ($Mode -eq 'Encrypt') { $xform = $aes.CreateEncryptor() }
    else                     { $xform = $aes.CreateDecryptor() }
    try { return , $xform.TransformFinalBlock($Data, 0, $Data.Length) }
    finally { $xform.Dispose() }
  }
  finally { $aes.Dispose() }
}

########################################
# Frame parser
########################################
function Script:ConvertFrom-Start2Frame {
  param([Parameter(Mandatory)] [byte[]]$Bytes)

  if ($Bytes.Length -lt 0x2C) { throw "file too short ($($Bytes.Length) bytes)" }

  for ($i = 0; $i -lt 16; $i++) {
    if ($Bytes[$i] -ne $Script:MAGIC_GUID[$i]) {
      $hex = ($Bytes[0..15] | ForEach-Object { '{0:x2}' -f $_ }) -join ''
      throw "bad magic GUID: $hex (not a start2.bin file?)"
    }
  }
  for ($i = 0; $i -lt 16; $i++) {
    if ($Bytes[16 + $i] -ne $Script:HEADER_CONST[$i]) {
      Write-Warning ("header constant differs at offset 0x{0:x2}" -f (16 + $i))
      break
    }
  }

  $ftLow  = [BitConverter]::ToUInt32($Bytes, 0x20)
  $ftHigh = [BitConverter]::ToUInt32($Bytes, 0x24)
  $totLen = [BitConverter]::ToUInt32($Bytes, 0x28)
  if ($totLen -lt $Script:PAD_TOTAL) { throw "bad total_len $totLen" }

  $cipherLen = [int]($totLen - $Script:PAD_TOTAL)
  if ($cipherLen -le 0 -or ($cipherLen % 16) -ne 0) {
    throw "ciphertext length ($cipherLen) isn't a valid AES block size multiple. File is likely corrupt or not a start2.bin"
  }

  $padSeed = [uint32]((($Script:PAD_SEED_CONST) -bxor [long]$ftLow) -band 0xFFFFFFFFL)
  $padMt   = [MT19937]::new($padSeed)
  $prePad  = [int]($padMt.Next() -band 0x1FF)
  $postPad = $Script:PAD_TOTAL - $prePad

  $cipherOff = 0x2C + $prePad
  if ($Bytes.Length -lt ($cipherOff + $cipherLen + $postPad)) { throw "file truncated" }

  $ct = New-Object 'byte[]' $cipherLen
  [Array]::Copy($Bytes, $cipherOff, $ct, 0, $cipherLen)

  return [PSCustomObject]@{
    FileTimeLow  = $ftLow
    FileTimeHigh = $ftHigh
    PrePadLen    = $prePad
    PostPadLen   = $postPad
    Ciphertext   = $ct
  }
}

########################################
# Decrypt
########################################
function Unprotect-StartMenuBin {
  param([Parameter(Mandatory)] [string]$FilePath)
  if (-not (Test-Path -LiteralPath $FilePath)) { throw "file not found: $FilePath"}
  $blob = [System.IO.File]::ReadAllBytes($FilePath)
  $info = ConvertFrom-Start2Frame -Bytes $blob
  $symSeed = [uint32]((([long]$info.FileTimeHigh) -bxor `
                       ([long]$info.FileTimeLow) -bxor `
                       $Script:PROV_KEY_DW0) -band 0xFFFFFFFFL)
  $sym = Get-SymmetricKey -SymSeed $symSeed

  return , (Invoke-AesCbcPkcs7 -Data $info.Ciphertext -Key $sym.Key -InitVector $sym.Iv -Mode Decrypt)
}

# Do not guess when Windows changes its private schema.
$plain = Unprotect-StartMenuBin -FilePath $InputPath
$json = [Text.Encoding]::UTF8.GetString($plain).TrimStart([char]0xFEFF)
$state = $json | ConvertFrom-Json
if ($null -eq $state.pinnedList) { throw 'Unknown Start menu schema: pinnedList is missing.' }
$nodes = @()
foreach ($pin in $state.pinnedList) {
  if ($pin.pinType -eq 2) {
    if ($null -eq $pin.items) { throw 'Unknown folder schema: items is missing.' }
    $children = @($pin.items | ForEach-Object {
      if (-not $_.id -or $_.pinType -eq 2) { throw 'Unknown folder item schema.' }
      [PSCustomObject]@{ kind = 'app'; id = $_.id }
    })
    $nodes += [PSCustomObject]@{ kind = 'folder'; name = $pin.name; items = $children }
  } elseif ($null -eq $pin.pinType -or $pin.pinType -eq 0) {
    if (-not $pin.id) { throw 'Unknown pin schema: id is missing.' }
    $nodes += [PSCustomObject]@{ kind = 'app'; id = $pin.id }
  } else { throw "Unknown pin type: $($pin.pinType)" }
}
$result = [PSCustomObject]@{ source = 'Windows Start pinned items'; items = $nodes }
$outputJson = $result | ConvertTo-Json -Depth 20
if ($OutputPath) {
  # CreateNew prevents accidentally overwriting configuration or Windows state.
  $fullPath = [IO.Path]::GetFullPath($OutputPath)
  $stream = [IO.File]::Open($fullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
  try { $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false)); try { $writer.Write($outputJson) } finally { $writer.Dispose() } }
  finally { $stream.Dispose() }
  Write-Host "Saved preview to $fullPath"
} else { $outputJson }

########################################
