using System;
using System.Collections.Generic;
using System.Linq;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// A self-contained <see cref="IMyInventory"/> fake with real behavior:
    /// items are stored in a list, volumes are computed from the real item
    /// definitions (via <see cref="ItemDefinitions"/>), and TransferItemTo
    /// actually moves items between fakes — so the KTZInv3 sorting logic can
    /// be exercised end to end.
    ///
    /// The game's real <see cref="Sandbox.Game.MyInventory"/> is NOT usable
    /// headless: its TransferItemTo walks conveyor endpoints and reads
    /// MySession.Static, which don't exist in unit tests.
    /// </summary>
    public class FakeInventory : IMyInventory
    {
        readonly List<MyInventoryItem> _items = new List<MyInventoryItem>();
        uint _nextId = 1;

        public MyFixedPoint MaxVolume { get; set; }

        /// <summary>When false, TransferItemTo/CanTransferItemTo return false (simulates no conveyor connection).</summary>
        public bool ConveyorConnected { get; set; } = true;

        /// <summary>Optional whitelist; when set, GetAcceptedItems returns only these types.</summary>
        public HashSet<MyItemType> AcceptedTypes { get; set; } = null;

        public FakeInventory(MyFixedPoint maxVolume)
        {
            MaxVolume = maxVolume;
        }

        // -- helpers for tests -------------------------------------------------

        public MyFixedPoint AmountOf(MyItemType type)
        {
            MyFixedPoint sum = 0;
            foreach (var it in _items)
                if (it.Type == type) sum += it.Amount;
            return sum;
        }

        /// <summary>Sum of ALL items in the inventory, across all types.</summary>
        public MyFixedPoint TotalAmount()
        {
            MyFixedPoint sum = 0;
            foreach (var it in _items) sum += it.Amount;
            return sum;
        }

        public int StackCount => _items.Count;

        public void AddItem(MyItemType type, MyFixedPoint amount)
        {
            // stack onto an existing entry of the same type when it won't exceed max stack
            var nfo = type.GetItemInfo();
            foreach (var it in _items)
            {
                if (it.Type == type && it.Amount + amount <= nfo.MaxStackAmount)
                {
                    var idx = _items.IndexOf(it);
                    _items[idx] = new MyInventoryItem(type, it.ItemId, it.Amount + amount);
                    return;
                }
            }
            _items.Add(new MyInventoryItem(type, _nextId++, amount));
        }

        public void Clear()
        {
            _items.Clear();
        }

        // -- IMyInventory -----------------------------------------------------

        public IMyEntity Owner => null;

        public bool IsFull => CurrentVolume >= MaxVolume;

        public MyFixedPoint CurrentMass
        {
            get
            {
                MyFixedPoint sum = 0;
                foreach (var it in _items)
                    sum += it.Type.GetItemInfo().Mass * it.Amount;
                return sum;
            }
        }

        public MyFixedPoint CurrentVolume
        {
            get
            {
                MyFixedPoint sum = 0;
                foreach (var it in _items)
                    sum += it.Type.GetItemInfo().Volume * it.Amount;
                return sum;
            }
        }

        public int ItemCount => _items.Count;

        public float VolumeFillFactor => MaxVolume == 0 ? 0 : (float)((double)CurrentVolume / (double)MaxVolume);

        public bool CanPutItems => true;

        public bool IsItemAt(int position) => position >= 0 && position < _items.Count;

        public MyFixedPoint GetItemAmount(MyItemType itemType)
        {
            MyFixedPoint sum = 0;
            foreach (var it in _items)
                if (it.Type == itemType) sum += it.Amount;
            return sum;
        }

        public bool ContainItems(MyFixedPoint amount, MyItemType itemType) => GetItemAmount(itemType) >= amount;

        public MyInventoryItem? GetItemAt(int index) => IsItemAt(index) ? _items[index] : (MyInventoryItem?)null;

        public MyInventoryItem? GetItemByID(uint id)
        {
            foreach (var it in _items)
                if (it.ItemId == id) return it;
            return null;
        }

        public MyInventoryItem? FindItem(MyItemType itemType)
        {
            foreach (var it in _items)
                if (it.Type == itemType) return it;
            return null;
        }

        public bool CanItemsBeAdded(MyFixedPoint amount, MyItemType itemType)
        {
            var vol = itemType.GetItemInfo().Volume * amount;
            return CurrentVolume + vol <= MaxVolume;
        }

        public void GetItems(List<MyInventoryItem> items, Func<MyInventoryItem, bool> filter = null)
        {
            foreach (var it in _items)
                if (filter == null || filter(it)) items.Add(it);
        }

        public bool TransferItemTo(IMyInventory dstInventory, MyInventoryItem item, MyFixedPoint? amount = null)
        {
            var fake = dstInventory as FakeInventory;
            if (fake == null || !ConveyorConnected || !fake.ConveyorConnected) return false;
            if (!CanTransferItemTo(dstInventory, item.Type)) return false;

            // find our copy of the item by id
            int srcIdx = -1;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].ItemId == item.ItemId) { srcIdx = i; break; }
            if (srcIdx < 0) return false;

            var amt = amount ?? item.Amount;
            if (amt > _items[srcIdx].Amount) amt = _items[srcIdx].Amount;
            if (amt <= 0) return false;

            var nfo = item.Type.GetItemInfo();
            var maxAccept = (MaxVolume - CurrentVolume) * (MyFixedPoint)(1.0 / (double)nfo.Volume);
            if (!nfo.UsesFractions) maxAccept = MyFixedPoint.Floor(maxAccept + (MyFixedPoint)0.001);
            if (amt > maxAccept) amt = maxAccept;
            if (amt <= 0) return false;

            // remove from source
            var remaining = _items[srcIdx].Amount - amt;
            if (remaining > 0)
                _items[srcIdx] = new MyInventoryItem(item.Type, item.ItemId, remaining);
            else
                _items.RemoveAt(srcIdx);

            // add to destination (stack onto existing)
            fake.AddItem(item.Type, amt);
            return true;
        }

        public bool TransferItemFrom(IMyInventory sourceInventory, MyInventoryItem item, MyFixedPoint? amount = null)
        {
            return sourceInventory.TransferItemTo(this, item, amount);
        }

        public bool TransferItemTo(IMyInventory dst, int sourceItemIndex, int? targetItemIndex = null, bool? stackIfPossible = null, MyFixedPoint? amount = null)
        {
            if (!IsItemAt(sourceItemIndex)) return false;
            return TransferItemTo(dst, _items[sourceItemIndex], amount);
        }

        public bool TransferItemFrom(IMyInventory sourceInventory, int sourceItemIndex, int? targetItemIndex = null, bool? stackIfPossible = null, MyFixedPoint? amount = null)
        {
            return sourceInventory.TransferItemTo(this, sourceItemIndex, targetItemIndex, stackIfPossible, amount);
        }

        public bool IsConnectedTo(IMyInventory otherInventory)
        {
            var fake = otherInventory as FakeInventory;
            return fake != null && ConveyorConnected && fake.ConveyorConnected;
        }

        public bool CanTransferItemTo(IMyInventory otherInventory, MyItemType itemType)
        {
            var fake = otherInventory as FakeInventory;
            if (fake == null || !ConveyorConnected || !fake.ConveyorConnected) return false;
            if (fake.AcceptedTypes != null && !fake.AcceptedTypes.Contains(itemType)) return false;
            return true;
        }

        public void GetAcceptedItems(List<MyItemType> itemsTypes, Func<MyItemType, bool> filter = null)
        {
            if (AcceptedTypes == null) return;
            foreach (var t in AcceptedTypes)
                if (filter == null || filter(t)) itemsTypes.Add(t);
        }
    }
}
