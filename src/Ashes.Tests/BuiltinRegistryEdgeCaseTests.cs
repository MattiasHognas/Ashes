using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class BuiltinRegistryEdgeCaseTests
{
    [Test]
    public void TryGetModule_should_return_false_for_nonexistent_module()
    {
        BuiltinRegistry.TryGetModule("Nonexistent.Module", out _).ShouldBeFalse();
    }

    [Test]
    public void TryGetModule_should_return_true_for_ashes_io()
    {
        BuiltinRegistry.TryGetModule("Ashes.IO", out var module).ShouldBeTrue();

        module.Name.ShouldBe("Ashes.IO");
        module.Members.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Ashes_IO_module_should_contain_expected_members()
    {
        BuiltinRegistry.TryGetModule("Ashes.IO", out var module).ShouldBeTrue();

        module.Members.ContainsKey("print").ShouldBeTrue();
        module.Members.ContainsKey("panic").ShouldBeTrue();
        module.Members.ContainsKey("args").ShouldBeTrue();
        module.Members.ContainsKey("write").ShouldBeTrue();
        module.Members.ContainsKey("writeLine").ShouldBeTrue();
        module.Members.ContainsKey("readLine").ShouldBeTrue();
    }

    [Test]
    public void Ashes_IO_print_should_be_callable_with_arity_1()
    {
        BuiltinRegistry.TryGetModule("Ashes.IO", out var module).ShouldBeTrue();

        var print = module.Members["print"];
        print.IsCallable.ShouldBeTrue();
        print.Arity.ShouldBe(1);
        print.Kind.ShouldBe(BuiltinRegistry.BuiltinValueKind.Print);
    }

    [Test]
    public void Ashes_IO_args_should_not_be_callable()
    {
        BuiltinRegistry.TryGetModule("Ashes.IO", out var module).ShouldBeTrue();

        var args = module.Members["args"];
        args.IsCallable.ShouldBeFalse();
        args.Arity.ShouldBe(0);
    }

    [Test]
    public void Ashes_File_module_should_contain_expected_members()
    {
        BuiltinRegistry.TryGetModule("Ashes.File", out var module).ShouldBeTrue();

        module.Members.ContainsKey("readText").ShouldBeTrue();
        module.Members.ContainsKey("writeText").ShouldBeTrue();
        module.Members.ContainsKey("exists").ShouldBeTrue();
    }

    [Test]
    public void Ashes_File_writeText_should_have_arity_2()
    {
        BuiltinRegistry.TryGetModule("Ashes.File", out var module).ShouldBeTrue();

        module.Members["writeText"].Arity.ShouldBe(2);
    }

    [Test]
    public void TryGetType_should_return_true_for_unit()
    {
        BuiltinRegistry.TryGetType("Unit", out var type).ShouldBeTrue();

        type.Name.ShouldBe("Unit");
        type.Constructors.Count.ShouldBe(1);
        type.Constructors[0].Name.ShouldBe("Unit");
    }

    [Test]
    public void TryGetType_should_return_true_for_maybe()
    {
        BuiltinRegistry.TryGetType("Maybe", out var type).ShouldBeTrue();

        type.Name.ShouldBe("Maybe");
        type.TypeParameters.Count.ShouldBe(1);
        type.Constructors.Count.ShouldBe(2);
        type.Constructors.ShouldContain(c => c.Name == "Some");
        type.Constructors.ShouldContain(c => c.Name == "None");
    }

    [Test]
    public void TryGetType_should_return_true_for_result()
    {
        BuiltinRegistry.TryGetType("Result", out var type).ShouldBeTrue();

        type.Name.ShouldBe("Result");
        type.TypeParameters.Count.ShouldBe(2);
        type.Constructors.Count.ShouldBe(2);
        type.Constructors.ShouldContain(c => c.Name == "Ok");
        type.Constructors.ShouldContain(c => c.Name == "Error");
    }

    [Test]
    public void TryGetType_should_return_false_for_nonexistent_type()
    {
        BuiltinRegistry.TryGetType("NonexistentType", out _).ShouldBeFalse();
    }

    [Test]
    public void IsBuiltinModule_should_return_true_for_known_modules()
    {
        BuiltinRegistry.IsBuiltinModule("Ashes").ShouldBeTrue();
        BuiltinRegistry.IsBuiltinModule("Ashes.IO").ShouldBeTrue();
        BuiltinRegistry.IsBuiltinModule("Ashes.File").ShouldBeTrue();
        BuiltinRegistry.IsBuiltinModule("Ashes.Text").ShouldBeTrue();
        BuiltinRegistry.IsBuiltinModule("Ashes.Http").ShouldBeTrue();
        BuiltinRegistry.IsBuiltinModule("Ashes.Net.Tcp").ShouldBeTrue();
        BuiltinRegistry.IsBuiltinModule("Ashes.Net.Tls").ShouldBeTrue();
    }

    [Test]
    public void IsBuiltinModule_should_return_false_for_unknown_module()
    {
        BuiltinRegistry.IsBuiltinModule("Unknown").ShouldBeFalse();
        BuiltinRegistry.IsBuiltinModule("Ashes.Unknown").ShouldBeFalse();
    }

    [Test]
    public void IsReservedModuleNamespace_should_return_true_for_ashes_prefix()
    {
        BuiltinRegistry.IsReservedModuleNamespace("Ashes").ShouldBeTrue();
        BuiltinRegistry.IsReservedModuleNamespace("Ashes.Anything").ShouldBeTrue();
        BuiltinRegistry.IsReservedModuleNamespace("Ashes.Deeply.Nested").ShouldBeTrue();
    }

    [Test]
    public void IsReservedModuleNamespace_should_return_false_for_non_ashes_names()
    {
        BuiltinRegistry.IsReservedModuleNamespace("MyModule").ShouldBeFalse();
        BuiltinRegistry.IsReservedModuleNamespace("Ashes2").ShouldBeFalse();
        BuiltinRegistry.IsReservedModuleNamespace("NotAshes.IO").ShouldBeFalse();
    }

    [Test]
    public void IsReservedTypeName_should_return_true_for_builtin_type_names()
    {
        BuiltinRegistry.IsReservedTypeName("Unit").ShouldBeTrue();
        BuiltinRegistry.IsReservedTypeName("Maybe").ShouldBeTrue();
        BuiltinRegistry.IsReservedTypeName("Result").ShouldBeTrue();
        BuiltinRegistry.IsReservedTypeName("Socket").ShouldBeTrue();
        BuiltinRegistry.IsReservedTypeName("TlsSocket").ShouldBeTrue();
        BuiltinRegistry.IsReservedTypeName("Float").ShouldBeTrue();
    }

    [Test]
    public void IsReservedTypeName_should_return_true_for_ashes_namespace()
    {
        BuiltinRegistry.IsReservedTypeName("Ashes").ShouldBeTrue();
    }

    [Test]
    public void IsReservedTypeName_should_return_false_for_user_types()
    {
        BuiltinRegistry.IsReservedTypeName("MyType").ShouldBeFalse();
        BuiltinRegistry.IsReservedTypeName("Color").ShouldBeFalse();
    }

    [Test]
    public void TryGetPrimitiveType_should_return_float_for_float_name()
    {
        BuiltinRegistry.TryGetPrimitiveType("Float", out var type).ShouldBeTrue();

        type.ShouldBeOfType<TypeRef.TFloat>();
    }

    [Test]
    public void TryGetPrimitiveType_should_return_false_for_non_primitive()
    {
        BuiltinRegistry.TryGetPrimitiveType("Int", out _).ShouldBeFalse();
        BuiltinRegistry.TryGetPrimitiveType("String", out _).ShouldBeFalse();
        BuiltinRegistry.TryGetPrimitiveType("Bool", out _).ShouldBeFalse();
    }

    [Test]
    public void StandardModuleNames_should_contain_all_known_modules()
    {
        var names = BuiltinRegistry.StandardModuleNames;

        names.ShouldContain("Ashes");
        names.ShouldContain("Ashes.IO");
        names.ShouldContain("Ashes.File");
        names.ShouldContain("Ashes.Text");
        names.ShouldContain("Ashes.Http");
        names.ShouldContain("Ashes.Net.Tcp");
        names.ShouldContain("Ashes.Net.Tls");
        names.ShouldContain("Ashes.Result");
        names.ShouldContain("Ashes.List");
        names.ShouldContain("Ashes.Maybe");
        names.ShouldContain("Ashes.Test");
    }

    [Test]
    public void Types_should_contain_builtin_types()
    {
        var types = BuiltinRegistry.Types;

        types.ShouldContain(t => t.Name == "Unit");
        types.ShouldContain(t => t.Name == "Maybe");
        types.ShouldContain(t => t.Name == "Result");
        types.ShouldContain(t => t.Name == "Socket");
        types.ShouldContain(t => t.Name == "TlsSocket");
    }

    [Test]
    public void Ashes_Result_module_should_have_resource_name()
    {
        BuiltinRegistry.TryGetModule("Ashes.Result", out var module).ShouldBeTrue();

        module.ResourceName.ShouldBe("Ashes.Semantics.StdLib.Ashes.Result.ash");
    }

    [Test]
    public void Ashes_List_module_should_have_resource_name()
    {
        BuiltinRegistry.TryGetModule("Ashes.List", out var module).ShouldBeTrue();

        module.ResourceName.ShouldBe("Ashes.Semantics.StdLib.Ashes.List.ash");
    }

    [Test]
    public void Ashes_Maybe_module_should_have_resource_name()
    {
        BuiltinRegistry.TryGetModule("Ashes.Maybe", out var module).ShouldBeTrue();

        module.ResourceName.ShouldBe("Ashes.Semantics.StdLib.Ashes.Maybe.ash");
    }

    [Test]
    public void Ashes_IO_module_should_have_null_resource_name()
    {
        BuiltinRegistry.TryGetModule("Ashes.IO", out var module).ShouldBeTrue();

        module.ResourceName.ShouldBeNull();
    }

    [Test]
    public void Ashes_Text_module_should_have_null_resource_name()
    {
        BuiltinRegistry.TryGetModule("Ashes.Text", out var module).ShouldBeTrue();

        module.ResourceName.ShouldBeNull();
    }
}
