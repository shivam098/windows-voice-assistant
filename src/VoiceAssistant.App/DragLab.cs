using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Board;

namespace VoiceAssistant.App;

/// <summary>
/// Drags idea-board notes with the real mouse (SendInput), in a window of its own with sample notes, and checks where
/// they land. It never touches the user's board. It moves the pointer, so run it only when nobody is using the PC:
/// <c>VoiceAssistant.exe --drag-lab out.txt</c>. Stops at the first failure, and before any drag that wouldn't start in
/// its own window.
/// </summary>
internal static class DragLab
{
    public static async Task RunAsync(string outputPath, Dispatcher ui)
    {
        var output = new StringBuilder();
        Window? window = null;
        BoardStore? board = null;
        try
        {
            await ui.InvokeAsync(() =>
            {
                board = new BoardStore(null);
                board.Add(BoardList.Ideas, "idea");
                foreach (var t in new[] { "a", "b", "c" })
                {
                    board.Add(BoardList.Todo, t);
                }

                window = new Window
                {
                    Title = "Voice Assistant drag lab",
                    Width = 1100,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    Content = new UI.IdeasPage(board),
                };
                window.Show();
                window.Activate();
            });
            await Task.Delay(1200).ConfigureAwait(false);

            Check(output, "start", Shown(board!, BoardList.Todo), "C,B,A");
            // Up within a list: A (bottom) to the top.
            if (!await DragAsync(output, ui, window!, "A", "C", above: true).ConfigureAwait(false) ||
                !Check(output, "A dragged to the top", Shown(board!, BoardList.Todo), "A,C,B"))
            {
                return;
            }

            // Down within a list: A to the bottom (the classic off-by-one).
            if (!await DragAsync(output, ui, window!, "A", "B", above: false).ConfigureAwait(false) ||
                !Check(output, "A dragged to the bottom", Shown(board!, BoardList.Todo), "C,B,A"))
            {
                return;
            }

            // One step down: C below B.
            if (!await DragAsync(output, ui, window!, "C", "B", above: false).ConfigureAwait(false) ||
                !Check(output, "C dragged one down", Shown(board!, BoardList.Todo), "B,C,A"))
            {
                return;
            }

            // To another list, between two notes.
            if (!await DragAsync(output, ui, window!, "Idea", "C", above: true).ConfigureAwait(false) ||
                !Check(output, "Idea dragged into To-do", Shown(board!, BoardList.Todo), "B,Idea,C,A") ||
                !Check(output, "Ideas now empty", Shown(board!, BoardList.Ideas), string.Empty))
            {
                return;
            }

            // Onto the column's "Add a note…" box: the top of that list.
            if (!await DragAsync(output, ui, window!, "A", null, above: true, addBoxOf: BoardList.Todo).ConfigureAwait(false) ||
                !Check(output, "A dropped on the add box", Shown(board!, BoardList.Todo), "A,B,Idea,C"))
            {
                return;
            }

            output.AppendLine("ALL PASS");
        }
        catch (Exception ex)
        {
            output.AppendLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await ui.InvokeAsync(() => window?.Close());
            await File.WriteAllTextAsync(outputPath, output.ToString()).ConfigureAwait(false);
        }
    }

    private static string Shown(BoardStore board, BoardList list) =>
        string.Join(",", BoardStore.Arrange(board.Items.Where(i => i.List == list && !i.Done), list).Select(i => i.Text));

    private static bool Check(StringBuilder output, string what, string actual, string expected)
    {
        var ok = actual == expected;
        output.AppendLine(ok ? $"PASS: {what}: {actual}" : $"FAIL: {what}: expected {expected}, got {actual}");
        return ok;
    }

    /// <summary>Presses on note [from], moves in small steps to the top or bottom quarter of note [to], and releases.</summary>
    private static async Task<bool> DragAsync(StringBuilder output, Dispatcher ui, Window window, string from, string? to, bool above, BoardList? addBoxOf = null)
    {
        var (start, end, ours) = await ui.InvokeAsync(() =>
        {
            var source = Card(window, from);
            FrameworkElement target = addBoxOf is { } list ? AddBox(window, list) : Card(window, to!);
            var s = source.PointToScreen(new Point(source.ActualWidth * 0.75, source.ActualHeight / 2));
            var e = target.PointToScreen(new Point(target.ActualWidth * 0.75, target.ActualHeight * (above ? 0.25 : 0.75)));
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var hit = WindowFromPoint(new POINT { X = (int)s.X, Y = (int)s.Y });
            return (s, e, GetAncestor(hit, 2) == handle); // the press lands in this window (it's topmost), nowhere else
        });
        if (!ours)
        {
            output.AppendLine("FAIL: the note isn't under the pointer in the lab window: not dragging");
            return false;
        }

        GetCursorPos(out var before);
        try
        {
            MoveTo(start);
            Thread.Sleep(80);
            Button(Win32.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(80);
            for (var k = 1; k <= 20; k++)
            {
                MoveTo(new Point(start.X + ((end.X - start.X) * k / 20), start.Y + ((end.Y - start.Y) * k / 20)));
                Thread.Sleep(25);
            }

            Thread.Sleep(150);
            Button(Win32.MOUSEEVENTF_LEFTUP);
        }
        finally
        {
            SetCursorPos(before.X, before.Y);
        }

        await Task.Delay(700).ConfigureAwait(false);
        return true;
    }

    private static TextBox AddBox(DependencyObject root, BoardList list) =>
        Find<TextBox>(root, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Add to " + UI.Theme.ListName(list));

    private static T Find<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T found && match(found))
            {
                return found;
            }

            for (var c = 0; c < VisualTreeHelper.GetChildrenCount(node); c++)
            {
                stack.Push(VisualTreeHelper.GetChild(node, c));
            }
        }

        throw new InvalidOperationException($"no {typeof(T).Name} on screen");
    }

    private static Border Card(DependencyObject root, string text)
    {
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is Border { Tag: BoardItem item } card && item.Text == text)
            {
                return card;
            }

            var children = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (var c = 0; c < children; c++)
            {
                stack.Push(VisualTreeHelper.GetChild(node, c));
            }
        }

        throw new InvalidOperationException($"no card “{text}” on screen");
    }

    private static void MoveTo(Point screen)
    {
        // Absolute coordinates over the whole virtual desktop (0..65535).
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        Send(new Win32.MOUSEINPUT
        {
            dx = (int)Math.Round((screen.X - left) * 65535.0 / (width - 1)),
            dy = (int)Math.Round((screen.Y - top) * 65535.0 / (height - 1)),
            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
        });
    }

    private static void Button(uint flag) => Send(new Win32.MOUSEINPUT { dwFlags = flag });

    private static void Send(Win32.MOUSEINPUT mi)
    {
        var input = new[] { new Win32.INPUT { type = Win32.INPUT_MOUSE, U = new Win32.InputUnion { mi = mi } } };
        Win32.SendInput(1, input, Marshal.SizeOf<Win32.INPUT>());
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

}
