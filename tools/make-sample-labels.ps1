<#
  Generates sample shipping-label PDFs (fictional names and addresses) for testing and screenshots.
  Usage: powershell -File tools\make-sample-labels.ps1 -OutDir samples
#>
param([string]$OutDir = (Join-Path $PSScriptRoot '..\samples'))
New-Item -ItemType Directory -Force $OutDir | Out-Null
$inv = [Globalization.CultureInfo]::InvariantCulture

function Write-Pdf([string]$path, [double]$pw, [double]$ph, [string]$content) {
    $objs = @(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>" -f $pw.ToString($inv), $ph.ToString($inv)),
        "<< /Length $($content.Length) >>`nstream`n$content`nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"
    )
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append("%PDF-1.4`n")
    $offs = @()
    for ($i = 0; $i -lt $objs.Count; $i++) { $offs += $sb.Length; [void]$sb.Append("$($i+1) 0 obj`n$($objs[$i])`nendobj`n") }
    $x = $sb.Length
    [void]$sb.Append("xref`n0 $($objs.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offs) { [void]$sb.Append((('{0:D10} 00000 n ' -f $o) + "`n")) }
    [void]$sb.Append("trailer << /Size $($objs.Count + 1) /Root 1 0 R >>`nstartxref`n$x`n%%EOF")
    [IO.File]::WriteAllText($path, $sb.ToString(), [Text.Encoding]::ASCII)
}

# A 4x6 label (288 x 432 pt) with its bottom-left corner at (ox, oy).
function Label-Content([double]$ox, [double]$oy, [string]$from, [string]$to, [int]$seed) {
    $c = New-Object Text.StringBuilder
    function PdfLine($s) { [void]$c.Append($s + "`n") }
    function PdfText($font, $size, $x, $y, $text) { PdfLine ("BT /{0} {1} Tf {2} {3} Td ({4}) Tj ET" -f $font, $size, ($ox + $x).ToString($inv), ($oy + $y).ToString($inv), $text) }
    function PdfRect($x, $y, $w, $h) { PdfLine ("{0} {1} {2} {3} re f" -f ($ox+$x).ToString($inv), ($oy+$y).ToString($inv), $w.ToString($inv), $h.ToString($inv)) }
    PdfLine "1.5 w"
    PdfLine ("{0} {1} 288 432 re S" -f $ox.ToString($inv), $oy.ToString($inv))
    PdfRect 8 352 72 72
    PdfLine "1 g"; PdfText F2 46 26 368 "P"; PdfLine "0 g"
    PdfText F2 14 96 404 "PRIORITY MAIL"
    PdfText F1 9 96 390 "US POSTAGE PAID  .  ZONE 5"
    PdfText F1 9 96 378 "Ship Date: 11/03/2026"
    PdfRect 0 346 288 2
    PdfText F2 16 60 326 "USPS PRIORITY MAIL"
    PdfRect 0 318 288 2
    $y = 300; foreach ($l in $from.Split('|')) { PdfText F1 9 10 $y $l; $y -= 11 }
    PdfText F2 10 40 250 "SHIP TO:"
    $y = 234; foreach ($l in $to.Split('|')) { PdfText F2 12 60 $y $l; $y -= 15 }
    PdfRect 0 182 288 3
    PdfText F2 10 70 166 "USPS TRACKING # EP"
    $x = 24.0
    for ($i = 0; $i -lt 70; $i++) {
        $seed = ($seed * 1103515245 + 12345) % 2147483648
        $w = 0.9 + ($seed % 4) * 0.9
        if ($x + $w -gt 264) { break }
        PdfRect $x 70 $w 88
        $x += $w + 0.9 + (($seed -shr 8) % 3) * 0.9
    }
    PdfText F2 11 52 52 "9405 5036 9930 0451 2210 87"
    PdfRect 0 40 288 3
    PdfText F1 8 10 22 "Electronic Rate Approved #038555749"
    return $c.ToString()
}

$from = "NORTHWIND GOODS|1200 MARKET ST STE 4|SAN FRANCISCO CA 94102"
$a = Label-Content 36 324 $from "JORDAN RIVERA|88 MAPLE GROVE RD|PORTLAND OR 97205-1120" 7
$c = $a + "BT /F2 12 Tf 36 280 Td (Instructions) Tj ET`n"
$y = 262
foreach ($line in @(
    "1. Print this page or a 4x6 thermal label. Fold or cut along the dotted line.",
    "2. Attach the label so the barcode is flat and fully visible.",
    "3. Drop the package at any USPS location or schedule a pickup.")) {
    $c += "BT /F1 9 Tf 36 $y Td ($line) Tj ET`n"; $y -= 16
}
Write-Pdf (Join-Path $OutDir 'sample-letter-page.pdf') 612 792 $c

Write-Pdf (Join-Path $OutDir 'sample-4x6.pdf') 288 432 (Label-Content 0 0 $from "SAM PATEL|415 HARBOR VIEW DR|AUSTIN TX 78701" 11)

$inner = Label-Content 0 0 $from "CASEY NGUYEN|9 WILLOW CT APT 2B|DENVER CO 80203" 3
Write-Pdf (Join-Path $OutDir 'sample-sideways.pdf') 612 792 ("q 0 -1 1 0 90 708 cm`n$inner`nQ`n")

$l1 = Label-Content 0 0 $from "TAYLOR BROOKS|302 CEDAR LN|COLUMBUS OH 43215" 5
$l2 = Label-Content 0 0 $from "MORGAN LEE|57 OAK PARK AVE|RALEIGH NC 27601" 9
Write-Pdf (Join-Path $OutDir 'sample-two-up.pdf') 612 792 ("q 1 0 0 1 9 330 cm`n$l1`nQ`nq 1 0 0 1 315 330 cm`n$l2`nQ`n")
"Wrote samples to $OutDir"
