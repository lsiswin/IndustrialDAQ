using Prism.Events;

namespace IndustrialDAQ.UI.Events;

/// <summary>模块向主窗口请求统一导航，确保菜单、标题和内容区域同步。</summary>
public sealed class NavigationRequestEvent : PubSubEvent<string>
{
}
