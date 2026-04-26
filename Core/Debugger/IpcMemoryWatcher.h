#pragma once
#include "pch.h"
#include "Shared/CpuType.h"
#include "Shared/MemoryOperationType.h"
#include <atomic>
#include <vector>
#include <memory>
#include <mutex>

namespace IpcWatchOpMask
{
	static constexpr uint32_t Read = 1u << 0;
	static constexpr uint32_t Write = 1u << 1;
	static constexpr uint32_t ExecOpCode = 1u << 2;
	static constexpr uint32_t ExecOperand = 1u << 3;
	static constexpr uint32_t DmaRead = 1u << 4;
	static constexpr uint32_t DmaWrite = 1u << 5;
	static constexpr uint32_t AllAccess = Read | Write | DmaRead | DmaWrite;
	static constexpr uint32_t AllWithExec = AllAccess | ExecOpCode | ExecOperand;
}

#pragma pack(push, 1)
// 24 bytes, wire-stable — mirrored in C# IpcMemEvent.
// 16-bit Value covers widest Mesen core bus width (SNES word). Wider cores
// would require bumping this field and the C# mirror.
struct IpcMemEvent
{
	uint64_t MasterClock;
	uint32_t Address;
	uint32_t AbsAddress;
	uint16_t Value;
	uint8_t  CpuTypeVal;
	uint8_t  MemType;
	uint8_t  OpType;
	uint8_t  AccessWidth;
	uint8_t  _pad[2];
};
#pragma pack(pop)
static_assert(sizeof(IpcMemEvent) == 24, "IpcMemEvent must be 24 bytes for C# marshaling");

struct IpcWatchRange
{
	uint32_t Start;
	uint32_t End;
	uint32_t OpMask;
	uint16_t ValueMin;   // inclusive; default 0
	uint16_t ValueMax;   // inclusive; default 0xFFFF (full 16-bit range = no filter)
	uint32_t SampleRate; // 0 or 1 = capture every matching event; N = 1-in-N
	mutable uint32_t SampleCounter;
};

class IpcMemoryWatcher
{
public:
	static constexpr size_t MinRingSize = 1024;
	static constexpr size_t MaxRingSize = 1u << 22; // 4M events = 96 MB cap
	static constexpr size_t DefaultRingSize = 1u << 16; // 65536

private:
	std::atomic<bool> _enabled{ false };

	// Per-CpuType range lists. Writers take _rangesLock and atomic-store a new
	// shared_ptr; readers atomic-load to get a snapshot, hot-path stays lock-free.
	std::shared_ptr<std::vector<IpcWatchRange>> _ranges[CpuTypeUtilities::GetCpuTypeCount()];
	std::mutex _rangesLock;

	std::unique_ptr<IpcMemEvent[]> _ring;
	size_t _ringMask = 0;
	std::atomic<uint64_t> _head{ 0 };
	std::atomic<uint64_t> _tail{ 0 };
	std::atomic<uint64_t> _dropped{ 0 };
	std::atomic<uint64_t> _highWater{ 0 };

	static size_t RoundUpPow2(size_t v);

public:
	IpcMemoryWatcher();
	~IpcMemoryWatcher();

	void SetRingSize(size_t size);
	size_t GetRingSize() const { return _ringMask + 1; }

	void SetEnabled(bool enabled) { _enabled.store(enabled, std::memory_order_relaxed); }
	bool IsEnabled() const { return _enabled.load(std::memory_order_relaxed); }

	void SetWatches(CpuType cpu, const IpcWatchRange* ranges, size_t count);
	void ClearAllWatches();

	// Match on 16-bit offset by default. SNES bank mirroring means the same
	// I/O register ($2118) appears as $002118, $802118, etc. Ranges are specified
	// as 16-bit offsets; if a range Start >= 0x10000 it's treated as a full 24-bit match.
	bool ShouldCapture(CpuType cpu, uint32_t addr, uint32_t value, MemoryOperationType opType) const
	{
		if(!_enabled.load(std::memory_order_relaxed)) {
			return false;
		}
		std::shared_ptr<std::vector<IpcWatchRange>> ranges =
			std::atomic_load_explicit(&_ranges[(int)cpu], std::memory_order_acquire);
		if(!ranges || ranges->empty()) {
			return false;
		}
		uint32_t opBit = OpTypeToMask(opType);
		if(!opBit) {
			return false;
		}
		uint32_t addr16 = addr & 0xFFFF;
		uint32_t val16 = value & 0xFFFF;
		for(const IpcWatchRange& r : *ranges) {
			// If range uses 16-bit addresses (Start < 0x10000), match on offset only
			uint32_t matchAddr = (r.Start < 0x10000) ? addr16 : addr;
			if(matchAddr < r.Start || matchAddr > r.End) continue;
			if(!(r.OpMask & opBit)) continue;
			if(val16 < r.ValueMin || val16 > r.ValueMax) continue;
			if(r.SampleRate > 1) {
				uint32_t n = ++r.SampleCounter;
				if(n % r.SampleRate != 0) continue;
			}
			return true;
		}
		return false;
	}

	void Push(const IpcMemEvent& e);

	size_t Drain(IpcMemEvent* out, size_t maxEvents, uint64_t& droppedOut, uint64_t& highWaterOut);

	static uint32_t OpTypeToMask(MemoryOperationType op);
};
