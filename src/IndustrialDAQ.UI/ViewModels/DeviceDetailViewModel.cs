// File: DeviceDetailViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using System.Windows;
using IndustrialDAQ.Acquisition;
using IndustrialDAQ.Core.Authorization;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Storage;
using IndustrialDAQ.UI.Models;
using IndustrialDAQ.UI.Services;
using IndustrialDAQ.UI.Events;

namespace IndustrialDAQ.UI.ViewModels;

public class DeviceDetailViewModel : BindableBase, IDestructible
{
    private readonly RealTimeStore _realTimeStore;
    private readonly AcquisitionHost _acquisitionHost;
    private readonly IDialogService _dialogService;
    private readonly IEventAggregator _eventAggregator;
    private readonly IResourceTreeService _resourceTreeService;
    private readonly IAuthManager _authManager;
    private readonly IAuthorizationService _authorizationService;
    private CancellationTokenSource? _cts;
    
    // TagId -> TagDisplayItem mapping for fast real-time updates
    private readonly Dictionary<string, TagDisplayItem> _itemLookup = new();

    public ObservableCollection<object> TreeRoots { get; } = new();

    public DelegateCommand<TagDisplayItem> WriteTagCommand { get; }
    public bool CanModify => _authManager.CanModify;

    public DeviceDetailViewModel(
        RealTimeStore realTimeStore, 
        AcquisitionHost acquisitionHost, 
        IDialogService dialogService, 
        IEventAggregator eventAggregator,
        IResourceTreeService resourceTreeService,
        IAuthManager authManager,
        IAuthorizationService authorizationService)
    {
        _realTimeStore = realTimeStore ?? throw new ArgumentNullException(nameof(realTimeStore));
        _acquisitionHost = acquisitionHost ?? throw new ArgumentNullException(nameof(acquisitionHost));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _resourceTreeService = resourceTreeService ?? throw new ArgumentNullException(nameof(resourceTreeService));
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        
        _cts = new CancellationTokenSource();
        WriteTagCommand = new DelegateCommand<TagDisplayItem>(OnWriteTagAsync, _ => CanModify);
        _authManager.CurrentUserChanged += OnCurrentUserChanged;
        
        InitializeTree();

        _ = SubscribeToChangesAsync(_cts.Token);
    }

    private void OnCurrentUserChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(CanModify));
        WriteTagCommand.RaiseCanExecuteChanged();
    }

    private void InitializeTree()
    {
        TreeRoots.Clear();
        _itemLookup.Clear();
        
        var snapshot = _resourceTreeService.Current;
        
        // 如果资源树数据库为空（尚未配置），则降级为使用采集主机设备数据
        if (snapshot.Count == 0)
        {
            InitializeTreeFromAcquisitionHost();
            return;
        }
        
        foreach (var root in snapshot.Roots)
        {
            var nodeItem = BuildNodeItem(root, snapshot);
            if (nodeItem != null)
            {
                TreeRoots.Add(nodeItem);
            }
        }
    }

    /// <summary>
    /// 当资源树数据库为空时的兜底方案：将 AcquisitionHost 的设备树映射为 ResourceTreeNodeItem 展示。
    /// </summary>
    private void InitializeTreeFromAcquisitionHost()
    {
        var devices = _acquisitionHost.GetDevices();
        foreach (var device in devices)
        {
            var deviceGroup = new ResourceTreeNodeItem
            {
                NodeId = device.Id,
                DisplayName = device.Name,
                NodeType = ResourceType.Device,
                Icon = "💻",
                IconColor = "#FACC15",
                IsExpanded = true
            };

            foreach (var tag in device.Tags)
            {
                var item = new TagDisplayItem(tag.Id)
                {
                    TagName = tag.Name,
                    Description = tag.Description ?? $"{device.Name}/{tag.Name}",
                    Value = "-",
                    Quality = "Init",
                    Timestamp = "-",
                    CanWrite = tag.Access != TagAccess.Read
                };
                _itemLookup[tag.Id] = item;
                deviceGroup.Children.Add(item);
            }

            TreeRoots.Add(deviceGroup);
        }
    }


    private object? BuildNodeItem(ResourceNode node, ResourceTreeSnapshot snapshot)
    {
        if (node.ResourceType == ResourceType.Tag)
        {
            // For Tag, it maps to TagDisplayItem
            // We need TagId. Let's assume TagId is node.Id or we extract it from Metadata.
            // But wait, the existing code says TagId is what matches TagValue.TagId.
            // We'll use node.Id as TagId.
            bool canWrite = true; // Default, actual value depends on tag config
            
            // Try to find if tag exists in AcquisitionHost to determine write access
            var devices = _acquisitionHost.GetDevices();
            foreach (var d in devices)
            {
                var tag = d.Tags.FirstOrDefault(t => t.Id == node.Id);
                if (tag != null)
                {
                    canWrite = tag.Access != TagAccess.Read;
                    break;
                }
            }

            var item = new TagDisplayItem(node.Id)
            {
                TagName = node.DisplayName,
                Value = "-",
                Quality = "Init",
                Timestamp = "-",
                CanWrite = canWrite,
                // Store resource path in description or a new property if available
                Description = node.Path.Value 
            };
            _itemLookup[node.Id] = item;
            return item;
        }
        else
        {
            // For folders (Factory, Area, Line, Cell, Device)
            var group = new ResourceTreeNodeItem
            {
                NodeId = node.Id,
                DisplayName = node.DisplayName,
                NodeType = node.ResourceType,
                Icon = GetIconForType(node.ResourceType),
                IconColor = GetColorForType(node.ResourceType),
                IsExpanded = node.ResourceType == ResourceType.Device || node.ResourceType == ResourceType.Line || node.ResourceType == ResourceType.Factory || node.ResourceType == ResourceType.Area
            };

            foreach (var child in snapshot.GetChildren(node.Path))
            {
                var childItem = BuildNodeItem(child, snapshot);
                if (childItem != null)
                {
                    group.Children.Add(childItem);
                }
            }
            return group;
        }
    }

    private string GetIconForType(ResourceType type)
    {
        return type switch
        {
            ResourceType.Factory => "🏭",
            ResourceType.Area => "🏢",
            ResourceType.Line => "🛣️",
            ResourceType.Cell => "⚙️",
            ResourceType.Device => "💻",
            _ => "📂"
        };
    }

    private string GetColorForType(ResourceType type)
    {
        return type switch
        {
            ResourceType.Factory => "#F43F5E",
            ResourceType.Line => "#8B5CF6",
            ResourceType.Device => "#FACC15",
            _ => "#38BDF8"
        };
    }

    public void Destroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task SubscribeToChangesAsync(CancellationToken ct)
    {
        try
        {
            var reader = _realTimeStore.Subscribe();
            await foreach (TagValue value in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Application.Current?.Dispatcher.Invoke(() => UpdateTagValue(value));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void UpdateTagValue(TagValue value)
    {
        if (_itemLookup.TryGetValue(value.TagId, out TagDisplayItem? item))
        {
            item.Value = value.Value?.ToString() ?? "-";
            item.Quality = value.Quality.ToString();
            item.Timestamp = value.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
        }
    }

    private async void OnWriteTagAsync(TagDisplayItem? item)
    {
        // 未登录和访客身份始终只读，不进入后续资源路径授权流程。
        if (!CanModify || item == null) return;
        
        // 当权限快照为空（未配置任何权限策略）时，直接放行（开放模式）
        bool permissionConfigured = _authorizationService.Current.Policies.Count > 0;
        
        if (permissionConfigured)
        {
            // 权限验证
            bool pathValid = ResourcePath.TryParse(item.Description, out var path);
            bool hasPermission = pathValid && 
                await _authorizationService.CanAsync(_authManager.CurrentUser.ToSubject(), path, "Write");
            
            if (!hasPermission)
            {
                // Popup Login Dialog
                bool loginSuccess = false;
                _dialogService.ShowDialog("LoginDialog", null, result =>
                {
                    if (result.Result == ButtonResult.OK)
                    {
                        loginSuccess = true;
                    }
                });

                if (!loginSuccess) return;

                // Recheck permission after login
                hasPermission = pathValid &&
                    await _authorizationService.CanAsync(_authManager.CurrentUser.ToSubject(), path, "Write");
                if (!hasPermission)
                {
                    _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                    {
                        Title = "权限拒绝",
                        Message = "当前用户没有向此测点写入数据的权限。",
                        Type = NotificationType.Error
                    });
                    return;
                }
            }
        }

        DeviceConfig? targetDevice = null;
        TagPoint? targetTag = null;
        
        foreach (var device in _acquisitionHost.GetDevices())
        {
            targetTag = device.Tags.FirstOrDefault(t => t.Id == item.TagId);
            if (targetTag != null)
            {
                targetDevice = device;
                break;
            }
        }
        
        if (targetTag == null || targetDevice == null) return;

        var parameters = new DialogParameters
        {
            { "TagName", item.TagName },
            { "DataType", targetTag.DataType },
            { "CurrentValue", item.Value }
        };

        _dialogService.ShowDialog("WriteTagDialog", parameters, result =>
        {
            if (result.Result != ButtonResult.OK) return;

            string stringValue = result.Parameters.GetValue<string>("ResultValue");
            if (string.IsNullOrWhiteSpace(stringValue)) return;

            object? writeValue = null;
            try
            {
                writeValue = targetTag.DataType switch
                {
                    TagDataType.Bool => bool.Parse(stringValue),
                    TagDataType.Int16 => short.Parse(stringValue),
                    TagDataType.Int32 => int.Parse(stringValue),
                    TagDataType.Float32 => float.Parse(stringValue),
                    TagDataType.Float64 => double.Parse(stringValue),
                    TagDataType.UInt16 => ushort.Parse(stringValue),
                    TagDataType.UInt32 => uint.Parse(stringValue),
                    TagDataType.String => stringValue,
                    _ => stringValue
                };
            }
            catch
            {
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "写入失败",
                    Message = $"写入格式不正确，无法转换为 {targetTag.DataType}",
                    Type = NotificationType.Error
                });
                return;
            }

            var driver = _acquisitionHost.GetDriver(targetDevice.Id);
            if (driver != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await driver.WriteTagAsync(targetTag, writeValue, CancellationToken.None);
                        _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                        {
                            Title = "写入成功",
                            Message = $"测点 [{targetTag.Name}] 写入指令已下发",
                            Type = NotificationType.Success
                        });
                    }
                    catch (Exception ex)
                    {
                        _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                        {
                            Title = "写入错误",
                            Message = ex.Message,
                            Type = NotificationType.Error
                        });
                    }
                });
            }
            else
            {
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "写入失败",
                    Message = "无法获取设备驱动实例，请检查设备是否连接",
                    Type = NotificationType.Error
                });
            }
        });
    }
}

public class ResourceTreeNodeItem : BindableBase
{
    private string _nodeId = string.Empty;
    public string NodeId { get => _nodeId; set => SetProperty(ref _nodeId, value); }

    private string _displayName = string.Empty;
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

    private ResourceType _nodeType;
    public ResourceType NodeType { get => _nodeType; set => SetProperty(ref _nodeType, value); }

    private string _icon = "📂";
    public string Icon { get => _icon; set => SetProperty(ref _icon, value); }

    private string _iconColor = "#FACC15";
    public string IconColor { get => _iconColor; set => SetProperty(ref _iconColor, value); }
    
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    public ObservableCollection<object> Children { get; } = new();
}
