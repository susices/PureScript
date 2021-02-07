﻿using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.CSharp.Syntax;

namespace Generater
{
    public class ClassGenerater : CodeGenerater
    {
        private TypeDefinition genType;

        private List<PropertyGenerater> properties = new List<PropertyGenerater>();
        private List<DelegateGenerater> events = new List<DelegateGenerater>();
        private List<MethodGenerater> methods = new List<MethodGenerater>();
        private List<TypeDefinition> nestType = new List<TypeDefinition>();
        private HashSet<string> refNameSpace = new HashSet<string>();
        private bool hasDefaultConstructor = false;
        private bool isFullValueType;
        private StreamWriter FileStream;

        public ClassGenerater(TypeDefinition type, StreamWriter writer = null)
        {
            genType = type;

            if (writer == null)
            {
                var filePath = Path.Combine(Binder.OutDir, $"Binder.{TypeFullName()}.cs");
                FileStream = File.CreateText(filePath);
            }
            else
            {
                FileStream = writer;
            }
            

            isFullValueType = Utils.IsFullValueType(genType);

            if(type.BaseType != null)
                refNameSpace.Add(type.BaseType.Namespace);

            foreach (var t in type.NestedTypes)
            {
                if (t.Name.StartsWith("<"))
                    continue;
                if ( CopyOrign(t) && (t.IsPublic || t.IsNestedPublic) && !Utils.IsObsolete(t))
                {
                    nestType.Add(t);
                    foreach (FieldDefinition field in t.Fields)
                    {
                        refNameSpace.Add(field.FieldType.Namespace);
                    }
                }
            }

            if(!isFullValueType)
            {
                foreach (FieldDefinition field in genType.Fields)
                {
                    if (field.IsPublic)
                    {
                        properties.Add(new PropertyGenerater(field));
                        refNameSpace.Add(field.FieldType.Namespace);
                    }
                    
                }
            }
            

            foreach(var e in genType.Events)
            {
                if(Utils.Filter(e))
                {
                    events.Add(new DelegateGenerater(e));
                    refNameSpace.Add(e.EventType.Namespace);
                }
            }

            foreach (PropertyDefinition prop in genType.Properties)
            {
                if (Utils.Filter(prop))
                {
                    var pt = prop.PropertyType.Resolve();
                    if (pt.IsDelegate())
                    {
                        events.Add(new DelegateGenerater(prop));
                    }
                    else
                    {
                        properties.Add(new PropertyGenerater(prop));
                        refNameSpace.Add(prop.PropertyType.Namespace);
                    }
                }
            }

            if(!genType.IsDelegate())
            {
                foreach (MethodDefinition method in genType.Methods)
                {
                    // if (isFullValueType && (method.Name.StartsWith("op_") || method.Name == "Equals"))
                    //     continue;
                    if ((method.IsPublic || genType.IsInterface) && !method.IsGetter && !method.IsSetter && !method.IsAddOn && !method.IsRemoveOn && Utils.Filter(method))
                    {
                        methods.Add(new MethodGenerater(method));
                        refNameSpace.UnionWith(Utils.GetNameSpaceRef(method));
                    }
                    if (method.IsConstructor && method.Parameters.Count == 0 && method.IsPublic)
                        hasDefaultConstructor = true;
                }
            }
        }

        public override string TypeFullName()
        {
            return genType.FullName.Replace("`","_");
        }

        private void GenNested()
        {
            if (nestType.Count <= 0)
                return;

            CS.Writer.Flush();
            foreach (var t in nestType)
            {
                new ClassGenerater(t, FileStream).Gen();
            }
        }

        public override void Gen()
        {

            using (new CS(new CodeWriter(FileStream)))
            {
                base.Gen();

                if (CopyOrign(genType))
                {
                    CopyGen(genType);
                    CS.Writer.EndAll();
                    return;
                }

                foreach (var ns in refNameSpace)
                {
                    if (!string.IsNullOrEmpty(ns))
                    {
                        CS.Writer.WriteLine($"using {ns}");
                        // if(!ns.StartsWith("System"))
                        //     CS.Writer.WriteLine($"using PS_{ns}");
                    }
                }
                CS.Writer.WriteLine("using System.Runtime.InteropServices");
                CS.Writer.WriteLine("using Object = UnityEngine.Object");

                

                if (!string.IsNullOrEmpty(genType.Namespace))
                {
                    CS.Writer.Start($"namespace {genType.Namespace}");
                }

                var flag = "public";
                if (genType.IsAbstract)
                    flag += " abstract";

                var classDefine = $"{flag} class {genType.Name}";

                if (genType.IsInterface)
                {
                   // classDefine += $" : WObject";
                }
                else if (genType.BaseType != null)
                {
                    string baseName = genType.BaseType.Name;
                    if (genType.BaseType.FullName == "System.Object")
                        baseName = "WObject";
                    else
                        Binder.AddType(genType.BaseType.Resolve());

                    classDefine += $" : {baseName}";
                }

                CS.Writer.Start(classDefine);

                GenNested();

                /*CS.Writer.Start($"internal {genType.Name}(int handle,IntPtr ptr): base(handle, ptr)");
                CS.Writer.End();*/

                foreach (var p in properties)
                {
                    p.Gen();
                }

                foreach(var e in events)
                {
                    e.Gen();
                }

                if(!hasDefaultConstructor && !genType.IsSealed)
                {
                    CS.Writer.WriteLine($"internal {genType.Name}()" + " { }", false);
                }
                foreach (var m in methods)
                {
                    m.Gen();
                }

                CS.Writer.EndAll();
            }
        }

        bool CopyOrign(TypeDefinition type)
        {
            if (type.IsGeneric() && !type.IsDelegate())
                return false;
            return type.IsValueType || type.IsEnum || type.IsDelegate() || type.IsInterface;
        }

        void CopyGen(TypeDefinition type )
        {

            bool isNested = type.IsNested;

            if (!(isNested && CopyOrign(genType.DeclaringType)))
            {
                var tName = type.FullName.Replace("/", "+");
                var name = new FullTypeName(tName);
                SyntaxTree syntaxTree;

                if (isNested)
                {
                    ITypeDefinition typeInfo = Binder.Decompiler.TypeSystem.MainModule.Compilation.FindType(name).GetDefinition();
                    var tokenOfFirstMethod = typeInfo.MetadataToken;
                    syntaxTree = Binder.Decompiler.Decompile(tokenOfFirstMethod);
                }
                else
                {
                    syntaxTree = Binder.Decompiler.DecompileType(name);
                }

                StringWriter w = new StringWriter();
                var outVisitor = new CustomOutputVisitor(isNested, w, Binder.DecompilerSetting.CSharpFormattingOptions);
                syntaxTree.AcceptVisitor(outVisitor);

                if (!isNested)
                {
                    foreach (var ns in outVisitor.nestedUsing)
                    {
                        CS.Writer.WriteHead($"using {ns}");
                    }
                }
                

                var txt = w.ToString();
                CS.Writer.WriteLine(txt, false);
            }

            if(genType.IsStruct())
            {
                foreach(var f in genType.Fields)
                {
                    var fType = f.FieldType.Resolve();
                    if (fType != null && !fType.IsPublic && !fType.IsNested)
                        Binder.AddType(fType);
                }

                if (!string.IsNullOrEmpty(genType.Namespace))
                {
                    CS.Writer.Start($"namespace {genType.Namespace}");
                }

                var classDefine = $"public partial struct {genType.Name}";

                CS.Writer.Start(classDefine);

                GenNested();

                foreach (var p in properties)
                {
                    p.Gen();
                }

                foreach (var m in methods)
                {
                    m.Gen();
                }

            }

        }
    }
}