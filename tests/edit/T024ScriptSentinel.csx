using System;
using System.Diagnostics;
using System.IO;

var root = Args.Count > 0 ? Args[0] : @"__MARKER_ROOT__";
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "script.marker"),
	"pid=" + Process.GetCurrentProcess().Id + ";utc=" + DateTime.UtcNow.ToString("O"));

