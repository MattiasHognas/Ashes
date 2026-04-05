using Ashes.Dap;
using Shouldly;

namespace Ashes.Tests;

public sealed class MiResponseParserTests
{
    [Test]
    public void ParseStackFrames_should_parse_single_frame()
    {
        var mi = """^done,stack=[frame={level="0",addr="0x401000",func="main",file="main.ash",fullname="/project/main.ash",line="5"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(1);
        frames[0].Id.ShouldBe(0);
        frames[0].Name.ShouldBe("main");
        frames[0].Line.ShouldBe(5);
        frames[0].Column.ShouldBe(0);
        frames[0].Source.ShouldNotBeNull();
        frames[0].Source!.Name.ShouldBe("main.ash");
        frames[0].Source!.Path.ShouldBe("/project/main.ash");
    }

    [Test]
    public void ParseStackFrames_should_parse_multiple_frames()
    {
        var mi = """^done,stack=[frame={level="0",addr="0x401000",func="foo",file="a.ash",fullname="/p/a.ash",line="3"},frame={level="1",addr="0x401100",func="bar",file="b.ash",fullname="/p/b.ash",line="10"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(2);
        frames[0].Name.ShouldBe("foo");
        frames[0].Line.ShouldBe(3);
        frames[1].Name.ShouldBe("bar");
        frames[1].Line.ShouldBe(10);
        frames[1].Id.ShouldBe(1);
    }

    [Test]
    public void ParseStackFrames_should_use_fallback_name_when_func_is_missing()
    {
        var mi = """^done,stack=[frame={level="2",addr="0x401000",file="a.ash",line="1"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(1);
        frames[0].Name.ShouldBe("frame 2");
        frames[0].Id.ShouldBe(2);
    }

    [Test]
    public void ParseStackFrames_should_handle_missing_file_with_fullname()
    {
        var mi = """^done,stack=[frame={level="0",func="main",fullname="/p/main.ash",line="1"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(1);
        frames[0].Source.ShouldNotBeNull();
        frames[0].Source!.Name.ShouldBeNull();
        frames[0].Source!.Path.ShouldBe("/p/main.ash");
    }

    [Test]
    public void ParseStackFrames_should_set_null_source_when_no_file_or_fullname()
    {
        var mi = """^done,stack=[frame={level="0",func="??",line="0"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(1);
        frames[0].Source.ShouldBeNull();
    }

    [Test]
    public void ParseStackFrames_should_default_line_to_zero_when_missing()
    {
        var mi = """^done,stack=[frame={level="0",func="main",file="a.ash",fullname="/p/a.ash"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(1);
        frames[0].Line.ShouldBe(0);
    }

    [Test]
    public void ParseStackFrames_should_return_empty_for_no_frames()
    {
        var frames = MiResponseParser.ParseStackFrames("^done,stack=[]");

        frames.ShouldBeEmpty();
    }

    [Test]
    public void ParseStackFrames_should_return_empty_for_unrelated_response()
    {
        var frames = MiResponseParser.ParseStackFrames("^running");

        frames.ShouldBeEmpty();
    }

    [Test]
    public void ParseLocals_should_parse_single_variable()
    {
        var mi = """^done,locals=[{name="x",value="42"}]""";

        var vars = MiResponseParser.ParseLocals(mi);

        vars.Length.ShouldBe(1);
        vars[0].Name.ShouldBe("x");
        vars[0].Value.ShouldBe("42");
        vars[0].VariablesReference.ShouldBe(0);
    }

    [Test]
    public void ParseLocals_should_parse_multiple_variables()
    {
        var mi = """^done,locals=[{name="x",value="42"},{name="y",value="hello"}]""";

        var vars = MiResponseParser.ParseLocals(mi);

        vars.Length.ShouldBe(2);
        vars[0].Name.ShouldBe("x");
        vars[0].Value.ShouldBe("42");
        vars[1].Name.ShouldBe("y");
        vars[1].Value.ShouldBe("hello");
    }

    [Test]
    public void ParseLocals_should_return_empty_for_no_locals()
    {
        var vars = MiResponseParser.ParseLocals("^done,locals=[]");

        vars.ShouldBeEmpty();
    }

    [Test]
    public void ParseLocals_should_return_empty_for_unrelated_response()
    {
        var vars = MiResponseParser.ParseLocals("^running");

        vars.ShouldBeEmpty();
    }

    [Test]
    public void ParseLocals_should_ignore_entries_with_missing_value()
    {
        // ParseLocals only matches entries with both name and value fields,
        // so an entry missing value is not parsed into a variable.
        var mi = """^done,locals=[{name="x"}]""";

        var vars = MiResponseParser.ParseLocals(mi);

        // No variable is returned because the expected
        // {name="...",value="..."} shape is not present.
        vars.ShouldBeEmpty();
    }

    [Test]
    public void ParseStackFrames_should_handle_non_numeric_level_gracefully()
    {
        var mi = """^done,stack=[frame={level="abc",func="main",file="a.ash",line="1"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(1);
        frames[0].Id.ShouldBe(0); // TryParse returns 0 on failure
        frames[0].Name.ShouldBe("main");
    }

    [Test]
    public void ParseStackFrames_should_use_file_as_path_when_fullname_is_missing()
    {
        var mi = """^done,stack=[frame={level="0",func="main",file="main.ash",line="1"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames[0].Source.ShouldNotBeNull();
        frames[0].Source!.Name.ShouldBe("main.ash");
        frames[0].Source!.Path.ShouldBe("main.ash");
    }
}
