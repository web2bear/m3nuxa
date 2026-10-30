param([Parameter(Mandatory)][string]$ResultPath)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = [System.Windows.Forms.Form]::new()
$form.Text = 'm3nuxa browser drag probe'
$form.StartPosition = 'Manual'
$form.TopMost = $true
$form.Size = [System.Drawing.Size]::new(720, 500)
$display = [System.Windows.Forms.Screen]::AllScreens | Where-Object { !$_.Primary } | Select-Object -First 1
if (!$display) { $display = [System.Windows.Forms.Screen]::PrimaryScreen }
$form.Location = [System.Drawing.Point]::new($display.WorkingArea.Left + 20, $display.WorkingArea.Top + 20)
$label = [System.Windows.Forms.Label]::new()
$label.Dock = 'Fill'; $label.Text = 'Drop browser data here'; $label.TextAlign = 'MiddleCenter'; $label.AllowDrop = $true
$form.Controls.Add($label)
$label.add_DragEnter([System.Windows.Forms.DragEventHandler]{ param($s,$e) $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy })
$label.add_DragDrop([System.Windows.Forms.DragEventHandler]{ param($s,$e)
    $data = $e.Data
    $url = $null
    if ($data.GetDataPresent('UniformResourceLocatorW', $false)) {
        $u = $data.GetData('UniformResourceLocatorW', $false)
        if ($u -is [System.IO.MemoryStream]) { $url = [System.Text.Encoding]::Unicode.GetString($u.ToArray()).TrimEnd([char]0) }
        elseif ($u -is [string]) { $url = $u }
    }
    $result = @{ formats = @($data.GetFormats($false)); text = [string]$data.GetData('UnicodeText'); url = $url }
    $result | ConvertTo-Json -Compress | Set-Content -LiteralPath $ResultPath -Encoding UTF8
})
[void]$form.ShowDialog()
