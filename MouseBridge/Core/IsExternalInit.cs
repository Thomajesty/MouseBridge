#if NETFRAMEWORK
// .NET Framework 4.8 里没有 IsExternalInit（记录类型/init 访问器编译时依赖它）。
// 补一个空壳，编译器就不再报 CS0518。只在 net48 目标下参与编译。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
