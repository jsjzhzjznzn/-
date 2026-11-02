using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 装备消息处理（35xx）
    /// 列表一次拉全（配置 + 实例 + 槽位），穿戴与卸下同一入口；
    /// 归属/角色/槽位校验与跨角色原子转移都在 EquipmentRepository 单事务内完成，这里只做会话校验与回执组装。
    /// </summary>
    public static class EquipmentHandler
    {
        /// <summary>装备列表：返回装备配置、本人实例（含穿戴者）与本人槽位快照。</summary>
        /// <param name="session">客户端会话</param>
        public static void HandleEquipmentList(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.EquipmentListResponse,
                    new EquipmentListResponse { Success = false, Message = "请先登录" });
                return;
            }

            EquipmentRepository repository = new();
            EquipmentRepository.EquipSnapshot snapshot = repository.Load(session.PlayerId);

            EquipmentListResponse response = new() { Success = true, Message = "OK" };
            FillConfigs(response.Configs, snapshot.Configs);
            FillInstances(response.Instances, snapshot.Instances);
            FillSlots(response.Slots, snapshot.Slots);

            Console.WriteLine($"装备列表返回:{session.PlayerId} {response.Configs.Count}条配置 / {response.Instances.Count}件实例 / {response.Slots.Count}个槽位");
            _ = session.Send(MessageID.EquipmentListResponse, response);
        }

        /// <summary>穿戴/卸下：instance_id=0 表示卸下该槽；回执回带最新槽位与实例，客户端整表刷新。</summary>
        /// <param name="session">客户端会话</param>
        /// <param name="body">protobuf 序列化的请求消息体</param>
        public static void HandleEquip(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.EquipResponse,
                    new EquipResponse
                    {
                        Success = false,
                        Message = "请先登录",
                        Code = EquipmentRepository.CodeNotLoggedIn,
                    });
                return;
            }

            EquipRequest request = EquipRequest.Parser.ParseFrom(body);

            EquipmentRepository repository = new();
            (bool success, int code, string message, bool unchanged) =
                repository.SetSlot(session.PlayerId, request.CharId, request.SlotType, request.InstanceId);

            EquipResponse response = new()
            {
                Success = success,
                Message = message,
                Code = code,
                Unchanged = unchanged,
            };

            // 失败也带回当前快照：客户端无需为刷新再发一次 3501
            EquipmentRepository.EquipSnapshot snapshot = repository.Load(session.PlayerId);
            FillInstances(response.Instances, snapshot.Instances);
            FillSlots(response.Slots, snapshot.Slots);

            Console.WriteLine($"穿戴/卸下:{session.PlayerId} 角色{request.CharId} 槽{request.SlotType} 实例{request.InstanceId} → {message}(code={code}{(unchanged ? " 幂等" : string.Empty)})");
            _ = session.Send(MessageID.EquipResponse, response);
        }

        /// <summary>配置表映射（列表回执用）。</summary>
        private static void FillConfigs(
            Google.Protobuf.Collections.RepeatedField<EquipConfigInfo> configs,
            List<EquipmentRepository.EquipConfig> source)
        {
            foreach (EquipmentRepository.EquipConfig config in source)
            {
                configs.Add(new EquipConfigInfo
                {
                    ItemId = config.ItemId,
                    SlotType = config.SlotType,
                    WeaponType = config.WeaponType,
                    AllowedChars = config.AllowedChars,
                    AttrJson = config.AttrJson,
                    ModelKey = config.ModelKey,
                });
            }
        }

        /// <summary>实例表映射（列表与穿戴共用）。</summary>
        private static void FillInstances(
            Google.Protobuf.Collections.RepeatedField<EquipInstanceInfo> instances,
            List<EquipmentRepository.EquipInstance> source)
        {
            foreach (EquipmentRepository.EquipInstance instance in source)
            {
                instances.Add(new EquipInstanceInfo
                {
                    InstanceId = instance.InstanceId,
                    ItemId = instance.ItemId,
                    EnhanceLevel = instance.EnhanceLevel,
                    AffixJson = instance.AffixJson,
                    EquippedChar = instance.EquippedChar,
                    EquippedSlot = instance.EquippedSlot,
                });
            }
        }

        /// <summary>槽位表映射（列表与穿戴共用）。</summary>
        private static void FillSlots(
            Google.Protobuf.Collections.RepeatedField<EquipSlotInfo> slots,
            List<EquipmentRepository.EquipSlot> source)
        {
            foreach (EquipmentRepository.EquipSlot slot in source)
            {
                slots.Add(new EquipSlotInfo
                {
                    CharId = slot.CharId,
                    SlotType = slot.SlotType,
                    InstanceId = slot.InstanceId,
                });
            }
        }
    }
}
