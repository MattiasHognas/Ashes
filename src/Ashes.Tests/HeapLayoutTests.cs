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
    public void Rc_header_precedes_value_without_changing_payload_offsets()
    {
        HeapHeaderLayoutDescriptor header = HeapLayouts.RcHeader;

        header.ReferenceCountOffsetBytes.ShouldBe(0);
        header.AllocationSizeOffsetBytes.ShouldBe(8);
        header.SizeBytes.ShouldBe(16);
        header.TotalAllocationSizeBytes(HeapLayouts.Adt.AllocationSizeBytes(2)).ShouldBe(40);
        HeapLayouts.Adt.TagOffsetBytes.ShouldBe(0);
        HeapLayouts.Adt.PayloadWordOffsetBytes(0).ShouldBe(8);
    }

    [Test]
    public void Layout_rejects_negative_payload_indexes_and_counts()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => HeapLayouts.Adt.PayloadWordOffsetBytes(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => HeapLayouts.Adt.AllocationSizeBytes(-1));
    }
}
