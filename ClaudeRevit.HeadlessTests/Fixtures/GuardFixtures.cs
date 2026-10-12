using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace System.Windows
{
    public static class MessageBox { public static void Show(string value) => throw new Exception("A GUI fixture must never execute."); }
    public class Window { public virtual void Show() => throw new Exception("A GUI fixture must never execute."); }
}

namespace mprHeadlessFixtures
{
    public static class Algorithms
    {
        public static int Sum(int a, int b) => a + b;
        public static void DirectUi() => System.Windows.MessageBox.Show("dialog");
        private static void Indirect() => DirectUi();
        public static void TransitiveUi() => Indirect();
        public static object NewWindow() => new System.Windows.Window();
        public static IEnumerable<int> DeferredUi() { System.Windows.MessageBox.Show("dialog"); yield return 1; }
        public static async Task<int> AsyncOperation() { await Task.Yield(); return 1; }
        public static object ReflectiveCall(MethodInfo method) => method.Invoke(null, null);
        public static void DelegateCall(Action action) => action();
        public static int VirtualCall(Base value) => value.Calculate();
        public static int StaticFieldCall() => UiInitializer.Value;
        public static object LateUi() => new LateUiEnumerable();
        [DllImport("user32.dll")] public static extern bool MessageBeep(uint type);
    }
    public class Base { public virtual int Calculate() => 1; }
    public class Derived : Base { public override int Calculate() { System.Windows.MessageBox.Show("dialog"); return 1; } }
    public static class UiInitializer { public static readonly int Value; static UiInitializer() { System.Windows.MessageBox.Show("dialog"); Value = 1; } }
    public sealed class LateUiEnumerable : System.Collections.IEnumerable
    {
        public System.Collections.IEnumerator GetEnumerator() => new LateUiIterator();
    }
    public sealed class LateUiIterator : System.Collections.IEnumerator
    {
        public object Current { get { System.Windows.MessageBox.Show("dialog"); return 1; } }
        public bool MoveNext() => true;
        public void Reset() { }
    }
}
