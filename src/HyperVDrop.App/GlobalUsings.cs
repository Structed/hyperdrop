// WinForms is referenced solely for NotifyIcon and the folder browser, which makes several core
// UI type names ambiguous. These aliases pin every one of them to the WPF version so individual
// files do not have to repeat the disambiguation.

global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using DataFormats = System.Windows.DataFormats;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DragEventArgs = System.Windows.DragEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

// WPF's implicit usings pull in System.Windows.Shapes, whose Path type collides with System.IO.Path.
global using Path = System.IO.Path;
