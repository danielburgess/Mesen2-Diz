#include "pch.h"
#include "Debugger/IpcMemoryWatcher.h"
#include "Shared/MemoryOperationType.h"
#include <algorithm>

size_t IpcMemoryWatcher::RoundUpPow2(size_t v)
{
	if(v == 0) return MinRingSize;
	size_t p = 1;
	while(p < v) p <<= 1;
	return p;
}

IpcMemoryWatcher::IpcMemoryWatcher()
{
	SetRingSize(DefaultRingSize);
}

IpcMemoryWatcher::~IpcMemoryWatcher() = default;

void IpcMemoryWatcher::SetRingSize(size_t size)
{
	size_t pow2 = RoundUpPow2(size);
	if(pow2 < MinRingSize) pow2 = MinRingSize;
	if(pow2 > MaxRingSize) pow2 = MaxRingSize;

	std::lock_guard<std::mutex> lock(_rangesLock);
	_ring.reset(new IpcMemEvent[pow2]);
	_ringMask = pow2 - 1;
	_head.store(0, std::memory_order_relaxed);
	_tail.store(0, std::memory_order_relaxed);
	_dropped.store(0, std::memory_order_relaxed);
	_highWater.store(0, std::memory_order_relaxed);
}

void IpcMemoryWatcher::SetWatches(CpuType cpu, const IpcWatchRange* ranges, size_t count)
{
	int idx = (int)cpu;
	if(idx < 0 || idx >= CpuTypeUtilities::GetCpuTypeCount()) {
		return;
	}

	std::shared_ptr<std::vector<IpcWatchRange>> newList;
	if(count > 0 && ranges) {
		newList = std::make_shared<std::vector<IpcWatchRange>>(ranges, ranges + count);
	}

	std::lock_guard<std::mutex> lock(_rangesLock);
	std::atomic_store_explicit(&_ranges[idx], newList, std::memory_order_release);
}

void IpcMemoryWatcher::ClearAllWatches()
{
	std::lock_guard<std::mutex> lock(_rangesLock);
	for(int i = 0; i < CpuTypeUtilities::GetCpuTypeCount(); i++) {
		std::shared_ptr<std::vector<IpcWatchRange>> empty;
		std::atomic_store_explicit(&_ranges[i], empty, std::memory_order_release);
	}
}

uint32_t IpcMemoryWatcher::OpTypeToMask(MemoryOperationType op)
{
	switch(op) {
		case MemoryOperationType::Read: return IpcWatchOpMask::Read;
		case MemoryOperationType::Write: return IpcWatchOpMask::Write;
		case MemoryOperationType::ExecOpCode: return IpcWatchOpMask::ExecOpCode;
		case MemoryOperationType::ExecOperand: return IpcWatchOpMask::ExecOperand;
		case MemoryOperationType::DmaRead: return IpcWatchOpMask::DmaRead;
		case MemoryOperationType::DmaWrite: return IpcWatchOpMask::DmaWrite;
		default: return 0;
	}
}

void IpcMemoryWatcher::Push(const IpcMemEvent& e)
{
	uint64_t head = _head.load(std::memory_order_relaxed);
	uint64_t tail = _tail.load(std::memory_order_acquire);
	uint64_t size = _ringMask + 1;

	if(head - tail >= size) {
		_dropped.fetch_add(1, std::memory_order_relaxed);
		return;
	}

	_ring[head & _ringMask] = e;
	_head.store(head + 1, std::memory_order_release);

	uint64_t fill = head + 1 - tail;
	uint64_t prevHw = _highWater.load(std::memory_order_relaxed);
	while(fill > prevHw && !_highWater.compare_exchange_weak(prevHw, fill, std::memory_order_relaxed)) {
		// loop until CAS succeeds or fill no longer exceeds high water
	}
}

size_t IpcMemoryWatcher::Drain(IpcMemEvent* out, size_t maxEvents, uint64_t& droppedOut, uint64_t& highWaterOut)
{
	uint64_t head = _head.load(std::memory_order_acquire);
	uint64_t tail = _tail.load(std::memory_order_relaxed);
	uint64_t avail = head - tail;
	size_t take = (size_t)std::min<uint64_t>(avail, maxEvents);

	for(size_t i = 0; i < take; i++) {
		out[i] = _ring[(tail + i) & _ringMask];
	}

	_tail.store(tail + take, std::memory_order_release);

	droppedOut = _dropped.exchange(0, std::memory_order_relaxed);
	highWaterOut = _highWater.exchange(0, std::memory_order_relaxed);
	return take;
}
