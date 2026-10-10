function Get-PeArchitecture {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Not a PE file: $Path" }
        $stream.Position = 0x3c
        $pe = $reader.ReadUInt32()
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        $machine = $reader.ReadUInt16()
        $sections = $reader.ReadUInt16()
        $stream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $stream.Position = $pe + 24
        $magic = $reader.ReadUInt16()
        if ($magic -notin 0x10b,0x20b) { throw "Unknown PE format: $Path" }
        $directoryOffset = if ($magic -eq 0x20b) { 112 } else { 96 }
        $stream.Position = $pe + 24 + $directoryOffset + 14 * 8
        $clrRva = $reader.ReadUInt32()
        $flags = 0
        if ($clrRva) {
            $stream.Position = $pe + 24 + $optionalSize
            for ($i = 0; $i -lt $sections; $i++) {
                $sectionStart = $stream.Position
                $stream.Position = $sectionStart + 8
                $virtualSize = $reader.ReadUInt32()
                $virtualAddress = $reader.ReadUInt32()
                $rawSize = $reader.ReadUInt32()
                $rawPointer = $reader.ReadUInt32()
                if ($clrRva -ge $virtualAddress -and $clrRva -lt ($virtualAddress + [Math]::Max($virtualSize,$rawSize))) {
                    $stream.Position = $rawPointer + $clrRva - $virtualAddress + 16
                    $flags = $reader.ReadUInt32()
                    break
                }
                $stream.Position = $sectionStart + 40
            }
            if (-not $flags) { throw "Cannot locate CLR header: $Path" }
        }
        $architecture = if ($clrRva -and ($flags -band 1) -and -not ($flags -band 2) -and
            -not ($flags -band 0x20000) -and $machine -eq 0x14c) { 'AnyCPU' }
        elseif ($machine -eq 0x8664) { 'x64' }
        elseif ($machine -eq 0x14c) { 'x86' }
        else { throw "Unsupported PE machine: $Path" }
        [pscustomobject]@{Path=$Path; Architecture=$architecture; Machine=$machine; Format=$magic; CLRFlags=$flags}
    } finally { $stream.Dispose() }
}
