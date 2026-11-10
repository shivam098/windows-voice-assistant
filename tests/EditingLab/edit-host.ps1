# A test window with one multi-line text box (a standard Windows edit control), for tests/EditingLab/run.ps1.
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Edit Lab'
$form.Width = 900; $form.Height = 400; $form.StartPosition = 'CenterScreen'
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true; $box.Dock = 'Fill'; $box.Font = New-Object System.Drawing.Font('Segoe UI', 14)
$box.AccessibleName = 'Message'
$form.Controls.Add($box)
$form.Add_Shown({ $form.Activate(); $box.Focus() })
[System.Windows.Forms.Application]::Run($form)
