﻿using System.Diagnostics;
using SEServer.Data;
using SEServer.Data.Interface;
using SEServer.Data.Message;
using SEServer.GameData;

namespace SEServer.Core;

public class ServerInstance
{
    private bool _endFlog = false;
    public ServerContainer ServerContainer { get; set; } = new ServerContainer();
    public UserManager UserManager { get; } = new UserManager();
    public ServerWorldConfig ServerWorldConfig { get; set; } = new ServerWorldConfig();
    public List<ServerWorld> Worlds { get; } = new List<ServerWorld>();
    public Time Time { get; } = new Time();
    
    public void StartGame()
    {
        Time.Init();
        
        ServerContainer.Init();
        ServerContainer.Start();
        
        Time.MaxFps = ServerContainer.Get<IWorldConfig>().FramePerSecond;

        ServerWorldConfig = (ServerWorldConfig)ServerContainer.Get<IWorldConfig>();
        
        // TODO: 测试性添加一个世界，后续修改
        var serverWorld = new ServerWorld();
        SetupWorld(serverWorld);

        MainLoop();
    }

    public void EndGame()
    {
        _endFlog = true;
    }

    private void MainLoop()
    {
        while (true)
        {
            Time.StartFrame();
            // 处理客户端消息
            HandleReceiveClientMessages();
            
            // 世界主循环
            foreach (var world in Worlds)
            {
                world.Update(Time.DeltaTime);
            }
            
            // 用户状态处理
            HandleUserState();

            // 发送客户端消息
            HandleSendClientMessages();
            
            if (_endFlog)
            {
                EndGame();
                return;
            }

            if (Time.CurFrame % (Time.MaxFps * 5) == 0)
            {
                var uploadBandwidth = ServerContainer.Get<IServerStatistics>().GetUploadBandwidthAndReset();
                ServerContainer.Get<ILogger>().LogInfo($"服务器信息： Fps: {Time.Fps:D2} " +
                                                       $"\t负载：{Time.LoadPercentage * 100:F}%" +
                                                       $" \t上传带宽：{uploadBandwidth / 1024f / 5:F}KB/s");
            }
            
            // 自旋
            Time.EndFrame();
        }
    }
    
    private void SetupWorld(ServerWorld world)
    {
        world.ServerContainer = ServerContainer;
        world.Init();
        Worlds.Add(world);
    }

    private void HandleReceiveClientMessages()
    {
        var serverNetworkService = ServerContainer.Get<IServerNetworkService>();
        var clientConnects = serverNetworkService.ClientConnects;
        foreach (var clientConnect in clientConnects)
        {
            HandleReceiveClientMessage(clientConnect);
        }
        
        serverNetworkService.RemoveUnconnectedClient();
    }

    private void HandleReceiveClientMessage(ClientConnect clientConnect)
    {
        if(clientConnect.State == ClientConnectState.Disconnected) 
            return;

        while (clientConnect.MessageQueue.TryDequeue(out var message))
        {
            try
            {
                if(message is AuthorizationMessage authorizationMessage)
                {
                    HandleAuthorizationMessage(clientConnect, authorizationMessage);
                    continue;
                }

                if (clientConnect.State != ClientConnectState.Authorized)
                {
                    // 未授权的客户端不接受其他消息
                    continue;
                }

                switch (message)
                {
                    case IWorldMessage worldMessage:
                        HandleWorldMessage(clientConnect.User, worldMessage);
                        break;
                    default:
                        ServerContainer.Get<ILogger>().LogError($"未知的消息类型，Id = {clientConnect.User.Id} Type = {message.GetType()}");
                        break;
                }
            }
            catch (Exception e)
            {
                ServerContainer.Get<ILogger>().LogError($"处理用户消息出错，Id = {clientConnect.User.Id}");
                ServerContainer.Get<ILogger>().LogError(e.ToString());
            }
        }
    }

    /// <summary>
    /// 处理用户认证
    /// </summary>
    /// <param name="clientConnect"></param>
    /// <param name="authorizationMessage"></param>
    private void HandleAuthorizationMessage(ClientConnect clientConnect, AuthorizationMessage authorizationMessage)
    {
        var userId = new UserId()
        {
            Id = authorizationMessage.UserId
        };
        
        // 先检查是否重复登录
        var user = UserManager.CreateOrGetUser(userId);
        if (user.ClientConnect != null)
        {
            // 重复登录，将之前的连接断开
            var oldClientConnect = user.ClientConnect;
            oldClientConnect.Disconnect();
            user.ClientConnect = clientConnect;
        }
        else
        {
            // 新登录
            user.ClientConnect = clientConnect;
        }
        user.NewClientConnect = true;

        ServerContainer.Get<ILogger>().LogInfo($"用户认证，Id = {user.Id}");
        
        clientConnect.SetAuthorized(user);
    }
    
    /// <summary>
    /// 处理用户状态
    /// </summary>
    private void HandleUserState()
    {
        for (var index = 0; index < UserManager.Users.Count; index++)
        {
            var user = UserManager.Users[index];

            HandleNewUserEnterWorld(user);
            if (HandleUserDisconnect(user))
            {
                UserManager.Users.RemoveAt(index);
                index--;
                continue;
            }
        }
    }
    
    private void HandleNewUserEnterWorld(User user)
    {
        ServerWorld? bindWorld = null;
        if (user is { NewClientConnect: true, ClientConnect: { State: ClientConnectState.Authorized } })
        {
            // 新连接
            user.NewClientConnect = false;

            bindWorld = null;
            if (user.BindWorld != WId.Invalid)
            {
                bindWorld = Worlds.FirstOrDefault(world => world.Id == user.BindWorld);
            }

            if (bindWorld == null)
            {
                // TODO: 临时绑定第一个世界，后续再改进为动态绑定
                bindWorld = Worlds[0];
                // 绑定世界
                user.BindWorld = bindWorld.Id;
                var player = bindWorld.PlayerManager.CreatePlayer();
                user.BindPlayer = player.Id;
            }

            // 发送快照信息
            var sendSnapshotMessage = new SnapshotMessage()
            {
                Snapshot = bindWorld.NearestSnapshot,
                PlayerId = user.BindPlayer
            };
            user.ClientConnect.SendMessage(sendSnapshotMessage);

            // 发送补偿信息
            foreach (var worldMessage in bindWorld.IncrementalStateInfo)
            {
                switch (worldMessage)
                {
                    case SyncEntityMessage syncEntityMessage:
                        user.ClientConnect.SendMessage(syncEntityMessage);
                        break;
                    case SubmitEntityMessage submitMessage:
                        user.ClientConnect.SendMessage(submitMessage);
                        break;
                    default:
                        ServerContainer.Get<ILogger>().LogError($"不支持的消息类型，Id = {user.Id} Type = {worldMessage.GetType()}");
                        break;
                }
            }
        }
    }
    
    private bool HandleUserDisconnect(User user)
    {
        if (!user.IsConnected)
        {
            user.Timeout += Time.DeltaTime;
            if (user.Timeout > ServerWorldConfig.TimeoutLimit)
            {
                // 超时断开连接
                var world = Worlds.FirstOrDefault(world => world.Id == user.BindWorld);
                world.SendPlayerMessage(new SubmitData()
                {
                    Type = PlayerSubmitGlobalMessageType.PLAYER_EXIT,
                    Arg0 = user.BindPlayer.Id
                });
                ServerContainer.Get<ILogger>().LogInfo($"用户超时断开连接，Id = {user.Id} PlayerId = {user.BindPlayer}");
                return true;
            }

            return false;
        }
        else
        {
            user.Timeout = 0;
            return false;
        }
    }

    /// <summary>
    /// 接收用户提交的实体信息
    /// </summary>
    /// <param name="user"></param>
    /// <param name="worldMessage"></param>
    private void HandleWorldMessage(User user, IWorldMessage worldMessage)
    {
        if (user.BindWorld == WId.Invalid)
        {
            return;
        }
        
        var world = Worlds.First(world => world.Id == user.BindWorld);
        world.ReceiveMessageQueue.Enqueue(worldMessage);
    }
    
    private void HandleSendClientMessages()
    {
        foreach (var world in Worlds)
        {
            var allUser = UserManager.Users.Where(user => user.BindWorld == world.Id).ToArray();
            while (world.SendMessageQueue.TryDequeue(out var message))
            {
                for (var index = 0; index < allUser.Length; index++)
                {
                    var user = allUser[index];
                    // 类型信息
                    switch (message)
                    {
                        case SyncEntityMessage syncEntityMessage:
                            TrySendWorldMessage(user, syncEntityMessage);
                            break;
                        case SnapshotMessage snapshotMessage:
                            TrySendWorldMessage(user, snapshotMessage);
                            break;
                        default:
                            ServerContainer.Get<ILogger>().LogError($"不支持的消息类型，Id = {user.Id} Type = {message.GetType()}");
                            break;
                    }
                }
            }
        }
    }

    private void TrySendWorldMessage<T>(User user, T message) where T : IWorldMessage
    {
        if(user.ClientConnect == null) 
            return;
        
        if(user.ClientConnect.State != ClientConnectState.Authorized)
            return;
            
        user.ClientConnect.SendMessage(message);
    }
}