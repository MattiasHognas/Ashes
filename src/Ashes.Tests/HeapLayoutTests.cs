using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class HeapLayoutTests
{
    [Test]
    public void Adt_layout_preserves_tag_fields_and_allocation_size()
    {
        HeapLayoutDescriptor layout = HeapLayouts.Adt;

        layout.TagOffsetBytes.ShouldBe(0);
        layout.PayloadWordOffsetBytes(0).ShouldBe(8);
        layout.PayloadWordOffsetBytes(2).ShouldBe(24);
        layout.AllocationSizeBytes(0).ShouldBe(8);
        layout.AllocationSizeBytes(3).ShouldBe(32);
    }

    [Test]
    public void List_layout_preserves_untagged_head_tail_cell()
    {
        HeapLayoutDescriptor layout = HeapLayouts.List;

        layout.TagOffsetBytes.ShouldBeNull();
        layout.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex).ShouldBe(0);
        layout.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex).ShouldBe(8);
        layout.FixedAllocationSizeBytes.ShouldBe(16);
    }

    [Test]
    public void Layout_rejects_negative_payload_indexes_and_counts()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => HeapLayouts.Adt.PayloadWordOffsetBytes(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => HeapLayouts.Adt.AllocationSizeBytes(-1));
    }
}
