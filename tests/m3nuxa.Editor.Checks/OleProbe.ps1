param([Parameter(Mandatory)][string]$ResultPath)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = [System.Windows.Forms.Form]::new()
$form.Text = 'm3nuxa OLE probe'
$form.StartPosition = 'Manual'
$form.TopMost = $true
$form.Size = [System.Drawing.Size]::new(720, 500)
$screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { !$_.Primary } | Select-Object -First 1
if (!$screen) { $screen = [System.Windows.Forms.Screen]::PrimaryScreen }
$form.Location = [System.Drawing.Point]::new($screen.WorkingArea.Left + 20, $screen.WorkingArea.Top + 20)
$form.AllowDrop = $true
$label = [System.Windows.Forms.Label]::new()
$label.Dock = 'Fill'
$label.Text = 'Drop file, link or text here'
$label.TextAlign = 'MiddleCenter'
$label.AllowDrop = $true
$form.Controls.Add($label)
$enter = [System.Windows.Forms.DragEventHandler]{ param($sender,$event)
    $event.Effect = [System.Windows.Forms.DragDropEffects]::Copy
}
$drop = [System.Windows.Forms.DragEventHandler]{ param($sender,$event)
    $formats = @($event.Data.GetFormats($false))
    $result = @{ formats = $formats; file = $event.Data.GetDataPresent('FileDrop'); text = $event.Data.GetDataPresent('UnicodeText'); url = $event.Data.GetDataPresent('UniformResourceLocatorW') }
    $result | ConvertTo-Json -Compress | Set-Content -LiteralPath $ResultPath -Encoding utf8
    $label.Text = 'Drop received'
}
$label.add_DragEnter($enter)
$label.add_DragDrop($drop)
[void]$form.ShowDialog()
