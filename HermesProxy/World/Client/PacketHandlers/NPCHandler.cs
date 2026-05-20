using Framework;
using Framework.GameMath;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_GOSSIP_MESSAGE)]
    void HandleGossipmessage(WorldPacket packet)
    {
        GossipMessagePkt gossip = new GossipMessagePkt();
        gossip.GossipGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = gossip.GossipGUID;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_4_0_8089))
            gossip.GossipID = packet.ReadInt32();
        else
            gossip.GossipID = (int)gossip.GossipGUID.GetEntry();

        gossip.TextID = packet.ReadInt32();

        uint optionsCount = packet.ReadUInt32();

        for (uint i = 0; i < optionsCount; i++)
        {
            ClientGossipOption option = new ClientGossipOption();
            option.OptionIndex = packet.ReadInt32();
            option.OptionIcon = packet.ReadUInt8();
            option.OptionFlags = (byte)(packet.ReadBool() ? 1 : 0); // Code Box

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                option.OptionCost = packet.ReadInt32();

            option.Text = packet.ReadCString();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                option.Confirm = packet.ReadCString();
            gossip.GossipOptions.Add(option);
        }

        uint questsCount = packet.ReadUInt32();

        for (uint i = 0; i < questsCount; i++)
        {
            ClientGossipQuest quest = ReadGossipQuestOption(packet);
            gossip.GossipQuests.Add(quest);
        }

        SendPacketToClient(gossip);
    }

    [PacketHandler(Opcode.SMSG_GOSSIP_COMPLETE)]
    void HandleGossipComplete(WorldPacket packet)
    {
        GossipComplete gossip = new GossipComplete();
        SendPacketToClient(gossip);
    }

    [PacketHandler(Opcode.SMSG_GOSSIP_POI)]
    void HandleGossipPoi(WorldPacket packet)
    {
        GossipPOI poi = new();
        poi.Flags = packet.ReadUInt32();
        var pos2d = packet.ReadVector2();
        poi.Pos = new Vector3(pos2d.X, pos2d.Y, 0);
        poi.Icon = packet.ReadUInt32();
        poi.Importance = packet.ReadUInt32();
        poi.Name = packet.ReadCString();
        SendPacketToClient(poi);
    }

    [PacketHandler(Opcode.SMSG_BINDER_CONFIRM)]
    void HandleBinderConfirm(WorldPacket packet)
    {
        BinderConfirm confirm = new BinderConfirm();
        confirm.Guid = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = confirm.Guid;
        SendPacketToClient(confirm);
    }

    [PacketHandler(Opcode.SMSG_VENDOR_INVENTORY)]
    void HandleVendorInventory(WorldPacket packet)
    {
        VendorInventory vendor = new VendorInventory();
        vendor.VendorGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = vendor.VendorGUID;
        byte itemsCount = packet.ReadUInt8();

        if (itemsCount == 0)
        {
            vendor.Reason = packet.ReadUInt8();
            SendPacketToClient(vendor);
            Log.Event("vendor.list.received", new
            {
                vendor_guid_low = vendor.VendorGUID.GetCounter(),
                item_count = 0,
                reason = vendor.Reason,
            });
            return;
        }

        int soldOutCount = 0;
        for (byte i = 0; i < itemsCount; i++)
        {
            VendorItem vendorItem = new();
            vendorItem.Slot = packet.ReadInt32();
            vendorItem.Item.ItemID = packet.ReadUInt32();
            packet.ReadUInt32(); // Display Id
            vendorItem.Quantity = packet.ReadInt32();
            vendorItem.Price = packet.ReadUInt32();
            vendorItem.Durability = packet.ReadInt32();
            vendorItem.StackCount = packet.ReadUInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                vendorItem.ExtendedCostID = packet.ReadInt32();
            GetSession().GameState.SetItemBuyCount(vendorItem.Item.ItemID, vendorItem.StackCount);
            if (vendorItem.Quantity == 0)
                soldOutCount++;
            vendor.Items.Add(vendorItem);

            // Diagnostic: dump every item's stock state at vendor open so we can
            // see whether the legacy server is dynamically decrementing Quantity
            // for sold-out slots (mangos convention: Quantity == 0 means out;
            // Quantity == -1/0xFFFFFFFF means unlimited).
            Log.Event("vendor.item", new
            {
                vendor_guid_low = vendor.VendorGUID.GetCounter(),
                slot = vendorItem.Slot,
                item_id = vendorItem.Item.ItemID,
                quantity = vendorItem.Quantity,
                stack_count = vendorItem.StackCount,
                price = vendorItem.Price,
                extended_cost = vendorItem.ExtendedCostID,
            });
        }

        SendPacketToClient(vendor);

        Log.Event("vendor.list.received", new
        {
            vendor_guid_low = vendor.VendorGUID.GetCounter(),
            item_count = (int)itemsCount,
            sold_out_count = soldOutCount,
        });
    }

    [PacketHandler(Opcode.SMSG_SHOW_BANK)]
    void HandleShowBank(WorldPacket packet)
    {
        ShowBank bank = new ShowBank();
        bank.Guid = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = bank.Guid;
        SendPacketToClient(bank);
    }

    [PacketHandler(Opcode.SMSG_TRAINER_LIST)]
    void HandleTrainerList(WorldPacket packet)
    {
        TrainerList trainer = new TrainerList();
        trainer.TrainerGUID = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = trainer.TrainerGUID;
        trainer.TrainerID = trainer.TrainerGUID.GetEntry();
        trainer.TrainerType = packet.ReadInt32();
        int count = packet.ReadInt32();
        for (int i = 0; i < count; ++i)
        {
            TrainerListSpell spell = new();
            uint spellId = packet.ReadUInt32();
            if (ModernVersion.ExpansionVersion > 1 &&
                LegacyVersion.ExpansionVersion <= 1)
            {
                // in vanilla the server sends learn spell with effect 36
                // in expansions the server sends the actual spell
                uint realSpellId = GameData.GetRealSpell(spellId);
                if (realSpellId != spellId)
                {
                    GetSession().GameState.StoreRealSpell(realSpellId, spellId);
                    spellId = realSpellId;
                }
            }
            spell.SpellID = spellId;
            TrainerSpellStateLegacy stateOld = (TrainerSpellStateLegacy)packet.ReadUInt8();
            TrainerSpellStateModern stateNew = stateOld.CastEnum<TrainerSpellStateModern>();
            spell.Usable = stateNew;
            spell.MoneyCost = packet.ReadUInt32();
            packet.ReadInt32(); // Profession Dialog
            packet.ReadInt32(); // Profession Button
            spell.ReqLevel = packet.ReadUInt8();
            spell.ReqSkillLine = packet.ReadUInt32();
            spell.ReqSkillRank = packet.ReadUInt32();
            spell.ReqAbility[0] = packet.ReadUInt32();
            spell.ReqAbility[1] = packet.ReadUInt32();
            spell.ReqAbility[2] = packet.ReadUInt32();
            trainer.Spells.Add(spell);
        }
        trainer.Greeting = packet.ReadCString();
        SendPacketToClient(trainer);
    }

    // JimsProxy: refresh the trainer list after a successful buy so the modern
    // client's trainer UI marks the freshly-learned spell as already-known.
    // Vanilla 1.12 server sends SMSG_TRAINER_BUY_SUCCEEDED { trainerGuid, spellId }
    // but the modern 1.14 client has no equivalent opcode and won't update its
    // trainer frame from SMSG_LEARNED_SPELL alone — the frame is populated from
    // SMSG_TRAINER_LIST and only re-renders when a new list arrives. Without this
    // re-request, the just-learned spell stays in the list and the player can
    // click it again (server returns SMSG_TRAINER_BUY_FAILED, repeat ad nauseam —
    // observed pattern: 9 FAILEDs in 1.7 sec from a rage-clicked spam). Mirrors
    // the same refresh pattern the AuctionHandler uses after auction operations.
    [PacketHandler(Opcode.SMSG_TRAINER_BUY_SUCCEEDED)]
    void HandleTrainerBuySucceeded(WorldPacket packet)
    {
        WowGuid64 trainerGuid64 = packet.ReadGuid();
        uint learnedSpellId = packet.ReadUInt32();
        Log.Event("spell.trainer_buy.refresh_list_requested", new
        {
            trainer_guid_low = trainerGuid64.GetCounter(),
            learned_spell_id = learnedSpellId,
        });
        WorldPacket refresh = new WorldPacket(Opcode.CMSG_TRAINER_LIST);
        refresh.WriteGuid(trainerGuid64);
        SendPacketToServer(refresh);
    }

    [PacketHandler(Opcode.SMSG_TRAINER_BUY_FAILED)]
    void HandleTrainerBuyFailed(WorldPacket packet)
    {
        TrainerBuyFailed buy = new();
        buy.TrainerGUID = packet.ReadGuid().To128(GetSession().GameState);
        buy.SpellID = packet.ReadUInt32();
        buy.TrainerFailedReason = packet.ReadUInt32();
        SendPacketToClient(buy);
        ChatPkt chat = new ChatPkt(GetSession(), ChatMessageTypeModern.System, $"Failed to learn Spell {buy.SpellID} (Reason {buy.TrainerFailedReason}).");
        SendPacketToClient(chat);

        // Ban defense: server explicitly rejected the buy, so the predecessor
        // was NOT removed server-side. Restore it to keep proxy state in sync.
        uint pendingSpellId = GetSession().GameState.PendingTrainerBuySpellId;
        uint removedPredecessor = GetSession().GameState.PendingTrainerBuyRemovedPredecessor;
        if (pendingSpellId != 0 && removedPredecessor != 0)
        {
            uint failedSpellId = buy.SpellID;
            uint failedLearnSpellId = GetSession().GameState.GetLearnSpellFromRealSpell(pendingSpellId);
            // Server may echo either the real spell id or the learn-wrapper id depending
            // on legacy expansion. Match on either to be safe.
            if (failedSpellId == pendingSpellId || failedSpellId == failedLearnSpellId)
            {
                GetSession().GameState.CurrentPlayerKnownSpells.Add(removedPredecessor);
                Log.Event("spell.trainer_buy.predecessor_restored_on_failed", new
                {
                    real_spell_id = pendingSpellId,
                    failed_spell_id = failedSpellId,
                    predecessor_restored = removedPredecessor,
                    reason = buy.TrainerFailedReason,
                });
            }
            GetSession().GameState.PendingTrainerBuySpellId = 0u;
            GetSession().GameState.PendingTrainerBuyRemovedPredecessor = 0u;
        }
    }

    [PacketHandler(Opcode.MSG_TALENT_WIPE_CONFIRM)]
    void HandleTalentWipeConfirm(WorldPacket packet)
    {
        RespecWipeConfirm respec = new();
        respec.TrainerGUID = packet.ReadGuid().To128(GetSession().GameState);
        respec.Cost = packet.ReadUInt32();
        SendPacketToClient(respec);
    }

    [PacketHandler(Opcode.SMSG_SPIRIT_HEALER_CONFIRM)]
    void HandleSpiritHealerConfirm(WorldPacket packet)
    {
        SpiritHealerConfirm confirm = new SpiritHealerConfirm();
        confirm.Guid = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(confirm);
    }
}
