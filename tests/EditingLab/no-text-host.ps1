# A test window whose focus is a list, not a text box (like a file list), for tests/EditingLab/run.ps1 -Lab NoTextBox.
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'No Text Lab'
$form.Width = 700; $form.Height = 400; $form.StartPosition = 'CenterScreen'
$list = New-Object System.Windows.Forms.ListBox
$list.Dock = 'Fill'; $list.Font = New-Object System.Drawing.Font('Segoe UI', 14)
$list.AccessibleName = 'Files'
[void]$list.Items.AddRange(@('report.docx', 'budget.xlsx', 'notes.txt'))
$list.SelectedIndex = 0
$form.Controls.Add($list)
$form.Add_Shown({ $form.Activate(); $list.Focus() })
[System.Windows.Forms.Application]::Run($form)
