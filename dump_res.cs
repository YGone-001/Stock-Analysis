using System;
using System.Reflection;
class P { static void Main() {
var asm = Assembly.LoadFile(@"E:\AIHelper-dev\src\bin\Debug\net8.0-windows\AIHelper.dll");
foreach(var r in asm.GetManifestResourceNames()) Console.WriteLine(r);
}}
