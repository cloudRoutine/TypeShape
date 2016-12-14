﻿#if TYPESHAPE_EXPOSE
module TypeShape
#else
module internal TypeShape
#endif

#nowarn "4224"

open System
open System.Collections.Generic
open System.Runtime.Serialization
open System.Reflection

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

//------------------------------------
// Section: TypeShape core definitions

/// Provides a simple breakdown of basic kinds of types.
/// Used for easier extraction of type shapes in the active pattern implementations.
[<NoEquality; NoComparison>]
type TypeShapeInfo =
    | Basic of Type
    | Enum of enumTy:Type * underlying:Type
    | Array of element:Type * rank:int
    | Generic of definition:Type * args:Type []

/// Used to extract the type variable contained in a specific shape
type ITypeShapeVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

/// Encapsulates a type variable that can be accessed using type shape visitors
[<AbstractClass>]
type TypeShape =
    [<CompilerMessage("TypeShape constructor should not be consumed.", 4224)>]
    internal new () = { }
    abstract Type : Type
    abstract ShapeInfo : TypeShapeInfo
    abstract Accept : ITypeShapeVisitor<'R> -> 'R
    override s.ToString() = sprintf "TypeShape [%O]" s.Type

/// Encapsulates a type variable that can be accessed using type shape visitors
[<Sealed>]
type TypeShape<'T> () =
    inherit TypeShape()
    static let shapeInfo =
        let t = typeof<'T>
        if t.IsEnum then
            Enum(t, Enum.GetUnderlyingType t)
        elif t.IsArray then
            Array(t.GetElementType(), t.GetArrayRank())
        elif t.IsGenericType then 
            Generic(t.GetGenericTypeDefinition(), t.GetGenericArguments())
        else
            Basic t
        
    override __.Type = typeof<'T>
    override __.ShapeInfo = shapeInfo
    override __.Accept v = v.Visit<'T> ()

exception UnsupportedShape of Type:Type
    with
    override __.Message = sprintf "Unsupported TypeShape '%O'" __.Type

[<AutoOpen>]
module private TypeShapeImpl =

    let allMembers =
        BindingFlags.NonPublic ||| BindingFlags.Public |||
            BindingFlags.Instance ||| BindingFlags.Static |||
                BindingFlags.FlattenHierarchy 

    let allInstanceMembers =
        BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Instance

    type MemberInfo with
        member inline m.ContainsAttr<'Attr when 'Attr :> Attribute> (inheritAttr) =
            m.GetCustomAttributes(inheritAttr)
            |> Array.exists (function :? 'Attr -> true | _ -> false)

        member inline m.TryGetAttribute<'Attr when 'Attr :> Attribute> (inheritAttr) =
            m.GetCustomAttributes(inheritAttr)
            |> Array.tryPick (function :? 'Attr as attr -> Some attr | _ -> None)

    let activateGeneric (templateTy:Type) (typeArgs : Type[]) (args:obj[]) =
        let templateTy =
            if typeArgs.Length = 0 then templateTy
            elif not templateTy.IsGenericType then invalidArg (string templateTy) "not generic."
            elif not templateTy.IsGenericTypeDefinition then
                templateTy.GetGenericTypeDefinition().MakeGenericType typeArgs
            else
                templateTy.MakeGenericType typeArgs

        let ctypes = args |> Array.map (fun o -> o.GetType())
        let ctor = templateTy.GetConstructor(allMembers, null, CallingConventions.Standard, ctypes, [||])
        ctor.Invoke args

    /// correctly resolves if type is assignable to interface
    let rec isInterfaceAssignableFrom (iface : Type) (ty : Type) =
        let proj (t : Type) = t.Assembly, t.Namespace, t.Name, t.MetadataToken
        if iface = ty then true
        elif ty.GetInterfaces() |> Array.exists(fun if0 -> proj if0 = proj iface) then true
        else
            match ty.BaseType with
            | null -> false
            | bt -> isInterfaceAssignableFrom iface bt

    let private canon = Type.GetType "System.__Canon"
    let private genShapeTy = typedefof<TypeShape<_>>

    let resolveTypeShape(typ : Type) =
        if typ = null then raise <| ArgumentNullException("TypeShape: System.Type cannot be null.")
        if typ.IsGenericTypeDefinition then raise <| UnsupportedShape typ
        elif typ.IsGenericParameter then raise <| UnsupportedShape typ
        elif typ = canon then raise <| UnsupportedShape typ
        elif typ.IsByRef || typ.IsPointer then raise <| UnsupportedShape typ
        else 
            let gt = genShapeTy.MakeGenericType [|typ|]
            Activator.CreateInstance gt :?> TypeShape

type Activator with
    /// Generic edition of the activator method which support type parameters and private types
    static member CreateInstanceGeneric<'Template>(?typeArgs : Type[], ?args:obj[]) : obj =
        let typeArgs = defaultArg typeArgs [||]
        let args = defaultArg args [||]
        activateGeneric typeof<'Template> typeArgs args

type Type with
    /// Correctly resolves if type is assignable to interface
    member iface.IsInterfaceAssignableFrom(ty : Type) : bool =
        isInterfaceAssignableFrom iface ty

type TypeShape with
    /// <summary>
    ///     Creates a type shape instance for given type
    /// </summary>
    /// <param name="typ">System.Type to be resolved.</param>
    static member Create(typ : Type) = resolveTypeShape typ
    /// <summary>
    ///     Creates a type shape instance for given type
    /// </summary>
    static member Create<'T>() = new TypeShape<'T>()

/// Creates a type shape instance for given type
let shapeof<'T> = TypeShape.Create<'T>()

//------------------------
// Section: Core BCL types

// Enum types

type IEnumVisitor<'R> =
    abstract Visit<'Enum, 'Underlying when 'Enum : enum<'Underlying>> : unit -> 'R

type IShapeEnum =
    abstract Accept : IEnumVisitor<'R> -> 'R

type private ShapeEnum<'Enum, 'Underlying when 'Enum : enum<'Underlying>>() =
    interface IShapeEnum with
        member __.Accept v = v.Visit<'Enum, 'Underlying> ()

// Nullable types

type INullableVisitor<'R> =
    abstract Visit<'T when 'T : (new : unit -> 'T) and 'T :> ValueType and 'T : struct> : unit -> 'R

type IShapeNullable =
    abstract Accept : INullableVisitor<'R> -> 'R

type private ShapeNullable<'T when 'T : (new : unit -> 'T) and 'T :> ValueType and 'T : struct> () =
    interface IShapeNullable with
        member __.Accept v = v.Visit<'T> ()


// Default Constructor types

type IDefaultConstructorVisitor<'R> =
    abstract Visit<'T when 'T : (new : unit -> 'T)> : unit -> 'R

type IShapeDefaultConstructor =
    abstract Accept : IDefaultConstructorVisitor<'R> -> 'R

type private ShapeDefaultConstructor<'T when 'T : (new : unit -> 'T)>() =
    interface IShapeDefaultConstructor with
        member __.Accept v = v.Visit<'T>()

// Equality Types
    
type IEqualityVisitor<'R> =
    abstract Visit<'T when 'T : equality> : unit -> 'R

type IShapeEquality =
    abstract Accept : IEqualityVisitor<'R> -> 'R

type private ShapeEquality<'T when 'T : equality>() =
    interface IShapeEquality with
        member __.Accept v = v.Visit<'T>()

// Comparison Types
    
type IComparisonVisitor<'R> =
    abstract Visit<'T when 'T : comparison> : unit -> 'R

type IShapeComparison =
    abstract Accept : IComparisonVisitor<'R> -> 'R

type private ShapeComparison<'T when 'T : comparison>() =
    interface IShapeComparison with
        member __.Accept v = v.Visit<'T>()

// Struct Types

type IStructVisitor<'R> =
    abstract Visit<'T when 'T : struct> : unit -> 'R

type IShapeStruct =
    abstract Accept : IStructVisitor<'R> -> 'R

type private ShapeStruct<'T when 'T : struct>() =
    interface IShapeStruct with
        member __.Accept v = v.Visit<'T>()

// Reference Types

type INotStructVisitor<'R> =
    abstract Visit<'T when 'T : not struct and 'T : null> : unit -> 'R

type IShapeNotStruct =
    abstract Accept : INotStructVisitor<'R> -> 'R

type private ShapeNotStruct<'T when 'T : not struct and 'T : null>() =
    interface IShapeNotStruct with
        member __.Accept v = v.Visit<'T>()

// Delegates

type IDelegateVisitor<'R> =
    abstract Visit<'Delegate when 'Delegate :> Delegate> : unit -> 'R

type IShapeDelegate =
    abstract Accept : IDelegateVisitor<'R> -> 'R

type private ShapeDelegate<'Delegate when 'Delegate :> Delegate>() =
    interface IShapeDelegate with
        member __.Accept v = v.Visit<'Delegate>()

// System.Tuple`1

type ITuple1Visitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeTuple1 =
    abstract Accept : ITuple1Visitor<'R> -> 'R

type private ShapeTuple1<'T> () =
    interface IShapeTuple1 with
        member __.Accept v = v.Visit<'T> ()

// System.Tuple`2

type ITuple2Visitor<'R> =
    abstract Visit<'T1, 'T2> : unit -> 'R

type IShapeTuple2 =
    abstract Accept : ITuple2Visitor<'R> -> 'R

type private ShapeTuple2<'T1, 'T2> () =
    interface IShapeTuple2 with
        member __.Accept v = v.Visit<'T1,'T2> ()

// System.Tuple`3

type ITuple3Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3> : unit -> 'R

type IShapeTuple3 =
    abstract Accept : ITuple3Visitor<'R> -> 'R

type private ShapeTuple3<'T1, 'T2, 'T3> () =
    interface IShapeTuple3 with
        member __.Accept v = v.Visit<'T1, 'T2, 'T3> ()

// System.Tuple`4

type ITuple4Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4> : unit -> 'R

type IShapeTuple4 =
    abstract Accept : ITuple4Visitor<'R> -> 'R

type private ShapeTuple4<'T1, 'T2, 'T3, 'T4> () =
    interface IShapeTuple4 with
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4> ()

// System.Tuple`5

type ITuple5Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5> : unit -> 'R

type IShapeTuple5 =
    abstract Accept : ITuple5Visitor<'R> -> 'R

type private ShapeTuple5<'T1, 'T2, 'T3, 'T4, 'T5> () =
    interface IShapeTuple5 with
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5> ()

// System.Tuple`6

type ITuple6Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> : unit -> 'R

type IShapeTuple6 =
    abstract Accept : ITuple6Visitor<'R> -> 'R

type private ShapeTuple6<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> () =
    interface IShapeTuple6 with
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> ()

// System.Tuple`7

type ITuple7Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> : unit -> 'R

type IShapeTuple7 =
    abstract Accept : ITuple7Visitor<'R> -> 'R

type private ShapeTuple7<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> () =
    interface IShapeTuple7 with
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> ()

// System.Tuple`8

type ITuple8Visitor<'R> =
    abstract Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7, 'TRest> : unit -> 'R

type IShapeTuple8 =
    abstract Accept : ITuple8Visitor<'R> -> 'R

type private ShapeTuple8<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7, 'TRest> () =
    interface IShapeTuple8 with
        member __.Accept v = v.Visit<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7, 'TRest> ()

// F# functions

type IFSharpFuncVisitor<'R> =
    abstract Visit<'Domain, 'CoDomain> : unit -> 'R

type IShapeFSharpFunc =
    abstract Accept : IFSharpFuncVisitor<'R> -> 'R

type private ShapeFSharpFunc<'Domain, 'CoDomain> () =
    interface IShapeFSharpFunc with
        member __.Accept v = v.Visit<'Domain, 'CoDomain> ()

// System.Exception

type IExceptionVisitor<'R> =
    abstract Visit<'exn when 'exn :> exn> : unit -> 'R

type IShapeException =
    abstract IsFSharpException : bool
    abstract Accept : IExceptionVisitor<'R> -> 'R

type private ShapeException<'exn when 'exn :> exn> (isFSharpExn : bool) =
    interface IShapeException with
        member __.IsFSharpException = isFSharpExn
        member __.Accept v = v.Visit<'exn> ()


//-----------------------------------
// Section: Collections & IEnumerable

// IEnumerable

type IEnumerableVisitor<'R> =
    abstract Visit<'Enum, 'T when 'Enum :> seq<'T>> : unit -> 'R

type IShapeEnumerable =
    abstract Accept : IEnumerableVisitor<'R> -> 'R

type private ShapeEnumerable<'Enum, 'T when 'Enum :> seq<'T>> () =
    interface IShapeEnumerable with
        member __.Accept v = v.Visit<'Enum, 'T> ()

// Collection

type ICollectionVisitor<'R> =
    abstract Visit<'Collection, 'T when 'Collection :> ICollection<'T>> : unit -> 'R

type IShapeCollection =
    abstract Accept : ICollectionVisitor<'R> -> 'R

type private ShapeCollection<'Collection, 'T when 'Collection :> ICollection<'T>> () =
    interface IShapeCollection with
        member __.Accept v = v.Visit<'Collection, 'T> ()

// KeyValuePair

type IKeyValuePairVisitor<'R> =
    abstract Visit<'K, 'V> : unit -> 'R

type IShapeKeyValuePair =
    abstract Accept : IKeyValuePairVisitor<'R> -> 'R

type private ShapeKeyValuePair<'K,'V> () =
    interface IShapeKeyValuePair with
        member __.Accept v = v.Visit<'K, 'V> ()

// System.Array

type IArrayVisitor<'R> =
    abstract Visit<'T> : rank:int -> 'R

type IShapeArray =
    abstract Accept : IArrayVisitor<'R> -> 'R

type private ShapeArray<'T>(rank : int) =
    interface IShapeArray with
        member __.Accept v = v.Visit<'T> rank

// System.Collections.List

type IResizeArrayVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeResizeArray =
    abstract Accept : IResizeArrayVisitor<'R> -> 'R

type private ShapeResizeArray<'T> () =
    interface IShapeResizeArray with
        member __.Accept v = v.Visit<'T> ()


// System.Collections.Dictionary

type IDictionaryVisitor<'R> =
    abstract Visit<'K, 'V when 'K : equality> : unit -> 'R

type IShapeDictionary =
    abstract Accept : IDictionaryVisitor<'R> -> 'R

type private ShapeDictionary<'K, 'V when 'K : equality> () =
    interface IShapeDictionary with
        member __.Accept v = v.Visit<'K, 'V> ()

// System.Collections.HashSet

type IHashSetVisitor<'R> =
    abstract Visit<'T when 'T : equality> : unit -> 'R

type IShapeHashSet =
    abstract Accept : IHashSetVisitor<'R> -> 'R

type private ShapeHashSet<'T when 'T : equality> () =
    interface IShapeHashSet with
        member __.Accept v = v.Visit<'T> ()

// F# Set

type IFSharpSetVisitor<'R> =
    abstract Visit<'T when 'T : comparison> : unit -> 'R

type IShapeFSharpSet =
    abstract Accept : IFSharpSetVisitor<'R> -> 'R

type private ShapeFSharpSet<'T when 'T : comparison> () =
    interface IShapeFSharpSet with
        member __.Accept v = v.Visit<'T> ()

// F# Map

type IFSharpMapVisitor<'R> =
    abstract Visit<'K, 'V when 'K : comparison> : unit -> 'R

type IShapeFSharpMap =
    abstract Accept : IFSharpMapVisitor<'R> -> 'R

type private ShapeFSharpMap<'K, 'V when 'K : comparison> () =
    interface IShapeFSharpMap with
        member __.Accept v = v.Visit<'K, 'V>()

// F# ref

type IShapeFSharpRef =
    abstract Accept : IFSharpRefVisitor<'R> -> 'R

and IFSharpRefVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type private ShapeFSharpRef<'T> () =
    interface IShapeFSharpRef with
        member __.Accept v = v.Visit<'T> ()

// F# option

type IFSharpOptionVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeFSharpOption =
    abstract Accept : IFSharpOptionVisitor<'R> -> 'R

type private ShapeFSharpOption<'T> () =
    interface IShapeFSharpOption with
        member __.Accept v = v.Visit<'T> ()

// F# List

type IFSharpListVisitor<'R> =
    abstract Visit<'T> : unit -> 'R

type IShapeFSharpList =
    abstract Accept : IFSharpListVisitor<'R> -> 'R

type private ShapeFSharpList<'T> () =
    interface IShapeFSharpList with
        member __.Accept v = v.Visit<'T> ()

// F# Choice`2

type IFSharpChoice2Visitor<'R> =
    abstract Visit<'T1,'T2> : unit -> 'R

type IShapeFSharpChoice2 =
    abstract Accept : IFSharpChoice2Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2> () =
    interface IShapeFSharpChoice2 with
        member __.Accept v = v.Visit<'T1,'T2>()

// F# Choice`3

type IFSharpChoice3Visitor<'R> =
    abstract Visit<'T1,'T2,'T3> : unit -> 'R

type IShapeFSharpChoice3 =
    abstract Accept : IFSharpChoice3Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3> () =
    interface IShapeFSharpChoice3 with
        member __.Accept v = v.Visit<'T1,'T2,'T3>()

// F# Choice`4

type IFSharpChoice4Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4> : unit -> 'R

type IShapeFSharpChoice4 =
    abstract Accept : IFSharpChoice4Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4> () =
    interface IShapeFSharpChoice4 with
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4>()

// F# Choice`5

type IFSharpChoice5Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4,'T5> : unit -> 'R

type IShapeFSharpChoice5 =
    abstract Accept : IFSharpChoice5Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4, 'T5> () =
    interface IShapeFSharpChoice5 with
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4,'T5>()

// F# Choice`6

type IFSharpChoice6Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4,'T5,'T6> : unit -> 'R

type IShapeFSharpChoice6 =
    abstract Accept : IFSharpChoice6Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4, 'T5, 'T6> () =
    interface IShapeFSharpChoice6 with
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4,'T5,'T6>()

// F# Choice`7

type IFSharpChoice7Visitor<'R> =
    abstract Visit<'T1,'T2,'T3,'T4,'T5,'T6,'T7> : unit -> 'R

type IShapeFSharpChoice7 =
    abstract Accept : IFSharpChoice7Visitor<'R> -> 'R

type private ShapeFSharpChoice<'T1, 'T2, 'T3, 'T4, 'T5, 'T6, 'T7> () =
    interface IShapeFSharpChoice7 with
        member __.Accept v = v.Visit<'T1,'T2,'T3,'T4,'T5,'T6,'T7>()

//-----------------------------
// Section: Member-based Shapes

[<AutoOpen>]
module private MemberUtils =

    let defaultOfUntyped (ty : Type) =
        TypeShape.Create(ty).Accept {
            new ITypeShapeVisitor<obj> with
                member __.Visit<'T>() = Unchecked.defaultof<'T> :> obj
        }

    let inline invalidMember (memberInfo : MemberInfo) =
        sprintf "TypeShape internal error: invalid MemberInfo '%O'" memberInfo
        |> invalidOp

    let isStructMember (path : MemberInfo[]) =
        path |> Array.exists (fun m -> m.DeclaringType.IsValueType)

    let isPublicMember (memberInfo : MemberInfo) =
        match memberInfo with
        | :? FieldInfo as f -> f.IsPublic
        | :? PropertyInfo as p -> p.GetGetMethod(true).IsPublic
        | _ -> invalidMember memberInfo

    let getMemberType (path : MemberInfo[]) =
        match path.[path.Length - 1] with
        | :? FieldInfo as f -> f.FieldType
        | :? PropertyInfo as p -> p.PropertyType
        | m -> invalidMember m
        
    let isWritableMember (path : MemberInfo[]) =
        path 
        |> Array.forall (fun m ->
            match m with
            | :? FieldInfo -> true
            | :? PropertyInfo as p -> p.CanWrite
            | _ -> invalidMember m)

    let inline getValue (obj:obj) (m:MemberInfo) =
        match m with
        | :? FieldInfo as f -> f.GetValue(obj)
        | :? PropertyInfo as p -> p.GetValue(obj, null)
        | _ -> invalidMember m

    let inline setValue (obj:obj) (m:MemberInfo) (value:obj) =
        match m with
        | :? FieldInfo as f -> f.SetValue(obj, value)
        | :? PropertyInfo as p -> p.SetValue(obj, value, null)
        | _ -> invalidMember m

    let inline project<'Record, 'Member> (path : MemberInfo[]) (value:'Record) =
        let mutable obj = box value
        for m in path do
            obj <- getValue obj m

        obj :?> 'Member

    let projectExpr<'Record, 'Member> (path : MemberInfo[]) (expr : Expr<'Record>) =
        let rec aux expr (m:MemberInfo) =
            match m with
            | :? FieldInfo as fI -> Expr.FieldGet(expr, fI)
            | :? PropertyInfo as pI -> Expr.PropertyGet(expr, pI)
            | _ -> invalidMember m

        Expr.Cast<'Member>(Array.fold aux (expr :> _) path) 

    let inline inject<'Record, 'Member> (isStructMember : bool) (path : MemberInfo[]) 
                                        (r : 'Record) (value : 'Member) =
        let mutable obj = box r
        let n = path.Length
        if isStructMember then
            let valueStack = Array.zeroCreate<obj> (n - 1)
            for i = 0 to n - 2 do
                valueStack.[i] <- obj
                obj <- getValue obj path.[i]

            setValue obj path.[n - 1] value
            for i = n - 2 downto 0 do
                let obj2 = valueStack.[i]
                setValue obj2 path.[i] obj
                obj <- obj2

            obj :?> 'Record          
        else
            for i = 0 to n - 2 do
                obj <- getValue obj path.[i]

            setValue obj path.[n - 1] value
            r

    let injectExpr (path : MemberInfo[]) 
                    (r : Expr<'TRecord>) 
                    (value : Expr<'MemberType>) =

        let rec aux i expr =
            if i = path.Length - 1 then
                match path.[i] with
                | :? FieldInfo as fI -> Expr.FieldSet(expr, fI, value)
                | :? PropertyInfo as pI -> Expr.PropertySet(expr, pI, value)
                | m -> invalidMember m
            else
                match path.[i] with
                | :? FieldInfo as fI -> aux (i+1) (Expr.FieldGet(expr, fI))
                | :? PropertyInfo as pI -> aux (i+1) (Expr.PropertyGet(expr, pI))
                | m -> invalidMember m
                
        Expr.Cast<unit>(aux 0 r)

    open System.Reflection.Emit
    open System.Collections.Concurrent

    type private Marker = class end

    let private compileDynamicMethod<'D when 'D :> Delegate> 
                (name : string) (argTypes : Type []) (returnType : Type)
                (ilBuilder : ILGenerator -> unit) =

        let dynamicMethod =
            new DynamicMethod(name, MethodAttributes.Static ||| MethodAttributes.Public, 
                                CallingConventions.Standard, returnType, argTypes, typeof<Marker>, 
                                skipVisibility = true)

        let ilGen = dynamicMethod.GetILGenerator()
        do ilBuilder ilGen
        dynamicMethod.CreateDelegate(typeof<'D>) :?> 'D

    let private compileCache = new ConcurrentDictionary<MemberInfo * string, Lazy<Delegate>>()
    let private compileDynamicMethodCached<'D when 'D :> Delegate> 
                (memberInfo : MemberInfo) (id : string) (argTypes : Type []) (returnType : Type)
                (ilBuilder : ILGenerator -> unit) =

        let wrapper = compileCache.GetOrAdd((memberInfo, id), fun _ -> lazy(compileDynamicMethod<'D> id argTypes returnType ilBuilder :> Delegate))
        lazy(wrapper.Value :?> 'D)

    let mkProjection<'DeclaringType, 'Property> (memberInfo : MemberInfo) (path : MemberInfo []) =
        let id = sprintf "%O-%O-projection" memberInfo.DeclaringType memberInfo
        let builder (gen : ILGenerator) =
            gen.Emit OpCodes.Ldarg_0
            for m in path do
                match m with
                | :? FieldInfo as f -> gen.Emit(OpCodes.Ldfld, f)
                | :? PropertyInfo as p -> 
                    let m = p.GetGetMethod(true)
                    if m.IsVirtual then
                        gen.EmitCall(OpCodes.Callvirt, m, null)
                    else
                        gen.EmitCall(OpCodes.Call, m, null)
                | _ -> invalidMember m

            gen.Emit OpCodes.Ret

        compileDynamicMethodCached<Func<'DeclaringType, 'Property>>
            memberInfo id [|typeof<'DeclaringType>|] typeof<'Property> builder

    let mkInjection<'DeclaringType, 'Property> (memberInfo : MemberInfo) (path : MemberInfo []) =
        let id = sprintf "%O-%O-injection" memberInfo.DeclaringType memberInfo
        let builder (gen : ILGenerator) =
            gen.Emit OpCodes.Ldarg_0
            for i = 0 to path.Length - 2 do
                match path.[i] with
                | :? FieldInfo as f -> gen.Emit(OpCodes.Ldfld, f)
                | :? PropertyInfo as p -> 
                    let m = p.GetGetMethod(true)
                    if m.IsVirtual then
                        gen.EmitCall(OpCodes.Callvirt, m, null)
                    else
                        gen.EmitCall(OpCodes.Call, m, null)
                | m -> invalidMember m

            gen.Emit OpCodes.Ldarg_1

            match path.[path.Length - 1] with
            | :? FieldInfo as f -> gen.Emit(OpCodes.Stfld, f)
            | :? PropertyInfo as p ->
                let m = p.GetSetMethod(true)
                if m.IsVirtual then
                    gen.EmitCall(OpCodes.Callvirt, m, null)
                else
                    gen.EmitCall(OpCodes.Call, m, null)

            | m -> invalidMember m

            gen.Emit OpCodes.Ldarg_0
            gen.Emit OpCodes.Ret

        compileDynamicMethodCached<Func<'DeclaringType, 'Property, 'DeclaringType>>
            memberInfo id [|typeof<'DeclaringType>; typeof<'Property>|] typeof<'DeclaringType> builder

    let mkUninitializedCtor<'DeclaringType> (ctor : MethodBase) =
        let id = sprintf "%O-%O-ctor" ctor.DeclaringType ctor
        let builder (gen : ILGenerator) =
            for p in ctor.GetParameters() do
                match p.ParameterType with
                | t when t.IsPrimitive -> gen.Emit OpCodes.Ldc_I4_0
                | t when t.IsValueType -> 
                    let l = gen.DeclareLocal t
                    gen.Emit(OpCodes.Ldloca_S, l.LocalIndex)
                    gen.Emit(OpCodes.Initobj, t)
                    gen.Emit(OpCodes.Ldloc, l.LocalIndex)
                | _ -> gen.Emit OpCodes.Ldnull

            match ctor with
            | :? ConstructorInfo as c -> gen.Emit(OpCodes.Newobj, c)
            | :? MethodInfo as m when m.IsStatic ->
                if m.IsVirtual then gen.EmitCall(OpCodes.Callvirt, m, null)
                else gen.EmitCall(OpCodes.Call, m, null)
            | _ -> invalidMember ctor

            gen.Emit OpCodes.Ret

        compileDynamicMethodCached<Func<'DeclaringType>> ctor id [||] typeof<'DeclaringType> builder

    let mkTagReader<'U> (tagReader : MemberInfo) =
        let id = sprintf "%O-%O-tagReader" tagReader.DeclaringType tagReader
        let builder (gen : ILGenerator) =
            gen.Emit OpCodes.Ldarg_0
            let m =
                match tagReader with
                | :? MethodInfo as m -> m
                | :? PropertyInfo as p -> p.GetGetMethod(true)
                | _ -> invalidMember tagReader

            if m.IsVirtual then gen.EmitCall(OpCodes.Callvirt, m, null)
            else gen.EmitCall(OpCodes.Call, m, null)
            gen.Emit OpCodes.Ret

        compileDynamicMethodCached<Func<'U, int>> tagReader id [|typeof<'U>|] typeof<int> builder
                

//-------------------------
// Member Shape Definitions

/// Identifies an instance member that defines
/// a value in a class instance, typically a field or property
type IShapeMember =
    /// Human-readable member identifier
    abstract Label : string
    /// The actual System.Reflection.MemberInfo corresponding to member
    abstract MemberInfo : MemberInfo
    /// Type of value stored by member
    abstract MemberType : Type
    /// True iff member is contained within a struct
    abstract IsStructMember : bool
    /// True iff member is public
    abstract IsPublic : bool

/// Identifies an instance member that defines
/// a value in a class instance, typically a field or property
type IShapeMember<'DeclaringType> =
    inherit IShapeMember
    abstract Accept : IMemberVisitor<'DeclaringType, 'R> -> 'R

/// Identifies an instance member that defines
/// a value in a class instance, typically a field or property
and ShapeMember<'DeclaringType, 'MemberType> private (label : string, memberInfo : MemberInfo, path : MemberInfo[]) =
    let isStructMember = isStructMember path
    let isPublicMember = isPublicMember memberInfo
    let projectFunc = mkProjection<'DeclaringType, 'MemberType> memberInfo path

    /// Human-readable member identifier
    member __.Label = label
    /// The actual System.Reflection.MemberInfo corresponding to member
    member __.MemberInfo = memberInfo
    /// True iff member is contained within a struct
    member __.IsStructMember = isStructMember
    /// True iff member is public
    member __.IsPublic = isPublicMember
    /// Projects an instance to member of given value
    member __.Project (instance : 'DeclaringType) = 
        projectFunc.Value.Invoke instance
        
    /// Projects an instance to member of given value
    member __.ProjectExpr (instance : Expr<'DeclaringType>) =
        projectExpr<'DeclaringType, 'MemberType> path instance

    interface IShapeMember<'DeclaringType> with
        member s.Label = label
        member s.MemberType = typeof<'MemberType>
        member s.MemberInfo = memberInfo
        member s.IsStructMember = isStructMember
        member s.IsPublic = isPublicMember
        member s.Accept v = v.Visit s

and IMemberVisitor<'DeclaringType, 'R> =
    abstract Visit<'MemberType> : ShapeMember<'DeclaringType, 'MemberType> -> 'R

//----------------------------
// Writable Member Definitions

/// Identifies an instance member that defines
/// a mutable value in a class instance, typically a field or property
type IShapeWriteMember<'Record> =
    inherit IShapeMember<'Record>
    abstract Accept : IWriteMemberVisitor<'Record,'R> -> 'R

/// Identifies an instance member that defines
/// a mutable value in a class instance, typically a field or property
and ShapeWriteMember<'DeclaringType, 'MemberType> private (label : string, memberInfo : MemberInfo, path : MemberInfo[], 
                                                            readOnly : ShapeMember<'DeclaringType, 'MemberType>) =
    let isStructMember = isStructMember path
    let isPublicMember = isPublicMember memberInfo
    let projectFunc = mkProjection<'DeclaringType, 'MemberType> memberInfo path
    let injectFunc = mkInjection<'DeclaringType, 'MemberType> memberInfo path

    /// Human-readable member identifier
    member __.Label = label
    /// The actual System.Reflection.MemberInfo corresponding to member
    member __.MemberInfo = memberInfo
    /// True iff member is contained within a struct
    member __.IsStructMember = isStructMember
    /// True iff member is public
    member __.IsPublic = isPublicMember
    /// Projects an instance to member of given value
    member __.Project (instance : 'DeclaringType) = 
        projectFunc.Value.Invoke instance
        
    /// Projects an instance to member of given value
    member __.ProjectExpr (instance : Expr<'DeclaringType>) =
        projectExpr<'DeclaringType, 'MemberType> path instance

    /// Injects a value to member of given instance
    member __.Inject (instance : 'DeclaringType) (field : 'MemberType) : 'DeclaringType =
        injectFunc.Value.Invoke(instance, field)

    /// Injects a value to member of given instance
    member __.InjectExpr (instance : Expr<'DeclaringType>) (field : Expr<'MemberType>) =
        injectExpr path instance field

    interface IShapeMember<'DeclaringType> with
        member s.Label = label
        member s.MemberType = typeof<'MemberType>
        member s.MemberInfo = memberInfo
        member s.IsStructMember = isStructMember
        member s.IsPublic = isPublicMember
        member s.Accept v = v.Visit readOnly

    interface IShapeWriteMember<'DeclaringType> with
        member s.Accept (v : IWriteMemberVisitor<'DeclaringType, 'R>) = v.Visit s

and IWriteMemberVisitor<'TRecord, 'R> =
    abstract Visit<'Field> : ShapeWriteMember<'TRecord, 'Field> -> 'R

//-------------------------------
// Constructor Shapes

/// Identifies a constructor implementation shape
type IShapeConstructor =
    /// Denotes whether constructor is public
    abstract IsPublic : bool
    /// Denotes the arity of the constructor arguments
    abstract Arity : int
    /// ConstructorInfo instance
    abstract ConstructorInfo : ConstructorInfo
    // A tuple type encoding all arguments passed to the constuctor
    abstract ArgumentsType : Type

/// Identifies a constructor implementation shape
and IShapeConstructor<'DeclaringType> =
    inherit IShapeConstructor
    abstract Accept : IShapeConstructorVisitor<'DeclaringType, 'R> -> 'R

/// Identifies a constructor implementation shape
and ShapeConstructor<'DeclaringType, 'CtorArgs> private (ctorInfo : ConstructorInfo, arity : int) =
    let valueReader = 
        match arity with
        | 0 -> fun _ -> [||]
        | 1 -> fun x -> [|x|]
        |_ -> FSharpValue.PreComputeTupleReader typeof<'CtorArgs>

    /// Creates an instance of declaring type with supplied constructor args
    member __.Invoke(args : 'CtorArgs) =
        let args = valueReader args
        ctorInfo.Invoke args :?> 'DeclaringType

    /// Creates an instance of declaring type with supplied constructor args
    member __.InvokeExpr(args : Expr<'CtorArgs>) : Expr<'DeclaringType> =
        let exprArgs = 
            match arity with
            | 1 -> [args :> Expr]
            | _ -> [for i in 0 .. arity - 1 -> Expr.TupleGet(args, i)]

        Expr.Cast<'DeclaringType>(Expr.NewObject(ctorInfo, exprArgs))

    interface IShapeConstructor<'DeclaringType> with
        member __.IsPublic = ctorInfo.IsPublic
        member __.Arity = arity
        member __.ConstructorInfo = ctorInfo
        member __.ArgumentsType = typeof<'CtorArgs>
        member __.Accept v = v.Visit __

and IShapeConstructorVisitor<'CtorType, 'R> =
    abstract Visit<'CtorArgs> : ShapeConstructor<'CtorType, 'CtorArgs> -> 'R

//---------------------------
// Supplementary Member utils

[<AutoOpen>]
module private MemberUtils2 =
    let mkMemberUntyped<'Record> (label : string) (memberInfo : MemberInfo) (path : MemberInfo[]) =
        let memberType = 
            match path.[path.Length - 1] with
            | :? FieldInfo as fI -> fI.FieldType
            | :? PropertyInfo as pI -> pI.PropertyType
            | m -> invalidMember m

        let tyArgs = [|typeof<'Record> ; memberType|]
        let args = [|box label; box memberInfo; box path|]
        let readOnly = Activator.CreateInstanceGeneric<ShapeMember<_,_>>(tyArgs, args)
        if isWritableMember path then
            Activator.CreateInstanceGeneric<ShapeWriteMember<_,_>>(tyArgs, Array.append args [|readOnly|])
            :?> IShapeMember<'Record>
        else
            readOnly :?> _
        
    let mkWriteMemberUntyped<'Record> (label : string) (memberInfo : MemberInfo) (path : MemberInfo[]) =
        match mkMemberUntyped<'Record> label memberInfo path with
        | :? IShapeWriteMember<'Record> as wm -> wm
        | _ -> invalidOp <| sprintf "TypeShape internal error: Member '%O' is not writable" memberInfo

    let mkCtorUntyped<'Record> (ctorInfo : ConstructorInfo) =
        let argTypes = ctorInfo.GetParameters() |> Array.map (fun p -> p.ParameterType)
        let arity = argTypes.Length
        let argumentType =
            match arity with
            | 0 -> typeof<unit>
            | 1 -> argTypes.[0]
            | _ -> FSharpType.MakeTupleType argTypes

        Activator.CreateInstanceGeneric<ShapeConstructor<_,_>>([|typeof<'Record>; argumentType|], [|box ctorInfo; box arity|])
        :?> IShapeConstructor<'Record>

//--------------------
// Generic Tuple Shape

[<AutoOpen>]
module private ShapeTupleImpl =

    type TupleInfo =
        { 
            Current : Type
            Fields : (PropertyInfo * FieldInfo) []
            Nested : (FieldInfo * TupleInfo) option
        }

    let rec mkTupleInfo (t : Type) =
        let props = t.GetProperties()
        let fields = t.GetFields(BindingFlags.NonPublic ||| BindingFlags.Instance)
        let fs, nested =
            if fields.Length = 8 then
                let nestedTuple = fields.[7]
                let nestedStruct = mkTupleInfo nestedTuple.FieldType
                Array.zip props.[..6] fields.[..6], Some(nestedTuple, nestedStruct)
            else
                Array.zip props fields, None

        { Current = t ; Fields = fs ; Nested = nested }

    let gatherTupleMembers (tI : TupleInfo) =
        let rec aux (ctx : MemberInfo list) (tI : TupleInfo) = seq {
            for p,f in tI.Fields do
                yield p, f :> MemberInfo :: ctx |> List.rev |> List.toArray

            match tI.Nested with
            | Some (fI,n) -> yield! aux (fI :> MemberInfo :: ctx) n
            | None -> ()
        }

        aux [] tI

    let gatherNestedFields (tI : TupleInfo) =
        let rec aux fs (tI : TupleInfo) =
            match tI.Nested with
            | Some (fI,n) -> aux (fI :: fs) n
            | _ -> fs

        aux [] tI 
//        |> List.rev 
        |> List.toArray

//---------------------------
// Shape Tuple Implementation

/// Denotes a specific System.Tuple shape
type IShapeTuple =
    /// Tuple element shape definitions
    abstract Elements : IShapeMember[]
    abstract Accept : ITupleVisitor<'R> -> 'R

and ITupleVisitor<'R> =
    abstract Visit : ShapeTuple<'Tuple> -> 'R 

/// Identifies a specific System.Tuple shape
and ShapeTuple<'Tuple> private () =
    let tupleInfo = mkTupleInfo typeof<'Tuple>

    let tupleElems =
        gatherTupleMembers tupleInfo
        |> Seq.mapi (fun i (pI, path) -> 
            let label = sprintf "Item%d" (i+1)
            mkWriteMemberUntyped<'Tuple> label pI path)
        |> Seq.toArray

    let fieldStack = gatherNestedFields tupleInfo

    /// Tuple element shape definitions
    member __.Elements = tupleElems
    /// Creates an uninitialized tuple instance of given type
    member __.CreateUninitialized() : 'Tuple =
        let obj = FormatterServices.GetUninitializedObject typeof<'Tuple>
        let mutable this = obj
        for f in fieldStack do
            let x = FormatterServices.GetUninitializedObject f.FieldType
            f.SetValue(this, x)
            this <- x

        obj :?> 'Tuple

    member __.CreateUninitializedExpr() : Expr<'Tuple> =
        let values = tupleElems |> Seq.map (fun e -> Expr.DefaultValue e.MemberType) |> Seq.toList
        Expr.Cast<'Tuple>(Expr.NewTuple(values))

    interface IShapeTuple with
        member __.Elements = tupleElems |> Array.map (fun e -> e :> _)
        member __.Accept v = v.Visit __

//---------------------
// F# Records

/// Denotes an F# record type
type IShapeFSharpRecord =
    /// F# record field shapes
    abstract Fields : IShapeMember[]
    abstract Accept : IFSharpRecordVisitor<'R> -> 'R

/// Identifies an F# record type
and ShapeFSharpRecord<'Record> private () =
    let ctorInfo = FSharpValue.PreComputeRecordConstructorInfo(typeof<'Record>, allMembers)
    let props = FSharpType.GetRecordFields(typeof<'Record>, allMembers)
//    let ctorParams = props |> Array.map (fun p -> defaultOfUntyped p.PropertyType)
    let fields = typeof<'Record>.GetFields(allInstanceMembers)
    let mkRecordField (prop : PropertyInfo) (field : FieldInfo) =
        mkWriteMemberUntyped<'Record> prop.Name prop [|field :> MemberInfo|]

    let ctorf = mkUninitializedCtor<'Record> ctorInfo

    let recordFields = Array.map2 mkRecordField props fields

    /// F# record field shapes
    member __.Fields = recordFields

    /// Creates an uninitialized instance for given record
    member __.CreateUninitialized() = 
        ctorf.Value.Invoke()
//        ctorInfo.Invoke(ctorParams) :?> 'Record

    member __.CreateUninitializedExpr() : Expr<'Record> =
        let values = props |> Seq.map (fun p -> Expr.DefaultValue p.PropertyType)
        Expr.Cast<'Record>(Expr.NewObject(ctorInfo, Seq.toList values))

    interface IShapeFSharpRecord with
        member __.Fields = recordFields |> Array.map unbox
        member __.Accept v = v.Visit __

and IFSharpRecordVisitor<'R> =
    abstract Visit : ShapeFSharpRecord<'Record> -> 'R

//----------------------
// F# Unions

/// Denotes an F# union case shape
type IShapeFSharpUnionCase =
    /// Underlying FSharp.Reflection.UnionCaseInfo description
    abstract CaseInfo : UnionCaseInfo
    /// Field shapes for union case
    abstract Fields : IShapeMember[]

/// Denotes an F# union case shape
type ShapeFSharpUnionCase<'U> private (uci : UnionCaseInfo) =
    let properties = uci.GetFields()
    let ctorInfo = FSharpValue.PreComputeUnionConstructorInfo(uci, allMembers)
    let ctorf = mkUninitializedCtor<'U> ctorInfo

//    let ctorParams = properties |> Array.map (fun p -> defaultOfUntyped p.PropertyType)

    let caseFields =
        match properties with
        | [||] -> [||]
        | _ ->
            let underlyingType = properties.[0].DeclaringType
            let fields = 
                underlyingType.GetFields(BindingFlags.NonPublic ||| BindingFlags.Instance)
                |> Array.filter (fun f -> f.Name <> "_tag")

            let mkField (fieldInfo : FieldInfo) (propertyInfo : PropertyInfo) =
                mkWriteMemberUntyped<'U> propertyInfo.Name propertyInfo [|fieldInfo|]

            Array.map2 mkField fields properties

    /// Underlying FSharp.Reflection.UnionCaseInfo description
    member __.CaseInfo = uci
    /// Field shapes for union case
    member __.Fields = caseFields

    /// Creates an uninitialized instance for specific union case
    member __.CreateUninitialized() : 'U =
        ctorf.Value.Invoke()
//        ctorInfo.Invoke(null, ctorParams) :?> 'U

    member __.CreateUninitializedExpr() : Expr<'U> =
        let fieldsExpr = properties |> Seq.map (fun p -> Expr.DefaultValue p.PropertyType)
        Expr.Cast<'U>(Expr.Call(ctorInfo, Seq.toList fieldsExpr))

    interface IShapeFSharpUnionCase with
        member __.CaseInfo = uci
        member __.Fields = caseFields |> Array.map (fun f -> f :> _)

/// Denotes an F# Union shape
type IShapeFSharpUnion =
    /// Case shapes for given union type
    abstract UnionCases : IShapeFSharpUnionCase[]
    abstract Accept : IFSharpUnionVisitor<'R> -> 'R

/// Denotes an F# Union shape
and ShapeFSharpUnion<'U> private () =
    let ucis = 
        FSharpType.GetUnionCases(typeof<'U>, allMembers)
        |> Array.map (fun uci -> 
            Activator.CreateInstanceGeneric<ShapeFSharpUnionCase<'U>>([||],[|uci|]) 
            :?> ShapeFSharpUnionCase<'U>)

//    let tagReader = FSharpValue.PreComputeUnionTagReader(typeof<'U>, allMembers)
    let tagReaderInfo = FSharpValue.PreComputeUnionTagMemberInfo(typeof<'U>, allMembers)
    let tagReader = mkTagReader<'U> tagReaderInfo

    let caseNames = ucis |> Array.map (fun u -> u.CaseInfo.Name)

    /// Case shapes for given union type
    member __.UnionCases = ucis
    /// Gets the underlying tag id for given union instance
    member __.GetTag (union : 'U) : int = tagReader.Value.Invoke union //tagReader union
    /// Gets the underlying tag id for given union case name
    member __.GetTag (caseName : string) : int =
        let caseNames = caseNames
        let n = caseNames.Length
        let mutable i = 0
        let mutable notFound = true
        while notFound && i < n do
            if caseNames.[i] = caseName then
                notFound <- false
            i <- i + 1
        if notFound then raise <| KeyNotFoundException(sprintf "Union case: %A" caseName)
        i

    member __.GetTagExpr (union : Expr<'U>) : Expr<int> =
        let expr =
            match tagReaderInfo with
            | :? MethodInfo as m when m.IsStatic -> Expr.Call(m, [union])
            | :? MethodInfo as m -> Expr.Call(union, m, [])
            | :? PropertyInfo as p -> Expr.PropertyGet(union, p)
            | _ -> invalidOp <| sprintf "Unexpected tag reader info %O" tagReaderInfo
        
        Expr.Cast<int> expr
        
        
    interface IShapeFSharpUnion with
        member __.UnionCases = ucis |> Array.map (fun u -> u :> _)
        member __.Accept v = v.Visit __

and IFSharpUnionVisitor<'R> =
    abstract Visit : ShapeFSharpUnion<'U> -> 'R

//------------------------
// C# Records

/// Denotes a type that behaves like a mutable C# record:
/// Carries a parameterless constructor and settable properties
type IShapeCliMutable =
    /// Gettable and Settable properties for C# Record
    abstract Properties : IShapeMember[]
    abstract Accept : ICliMutableVisitor<'R> -> 'R

/// Denotes a type that behaves like a C# record:
/// Carries a parameterless constructor and settable properties
and ShapeCliMutable<'Record> private (defaultCtor : ConstructorInfo) =
    let properties =
        typeof<'Record>.GetProperties(allInstanceMembers)
        |> Seq.filter (fun p -> p.CanRead && p.CanWrite && p.GetIndexParameters().Length = 0)
        |> Seq.map (fun p -> mkWriteMemberUntyped<'Record> p.Name p [|p|])
        |> Seq.toArray

    let ctor = mkUninitializedCtor<'Record> defaultCtor

    /// Creates an uninitialized instance for given C# record
    member __.CreateUninitialized() = ctor.Value.Invoke()
    /// Creates an uninitialized instance for given C# record
    member __.CreateUninitializedExpr() = 
        Expr.Cast<'Record>(Expr.NewObject(defaultCtor, []))

    /// Property shapes for C# record
    member __.Properties = properties

    interface IShapeCliMutable with
        member __.Properties = properties |> Array.map (fun p -> p :> _)
        member __.Accept v = v.Visit __

and ICliMutableVisitor<'R> =
    abstract Visit : ShapeCliMutable<'Record> -> 'R

//--------------------------
// Shape POCO

/// Denotes any .NET type that is either a class or a struct
type IShapePoco =
    /// True iff POCO is a struct
    abstract IsStruct : bool
    /// Constructor shapes for the type
    abstract Constructors : IShapeConstructor[]
    /// Field shapes for the type
    abstract Fields : IShapeMember[]
    /// Property shapes for the type
    abstract Properties : IShapeMember[]
    abstract Accept : IPocoVisitor<'R> -> 'R

/// Denotes any .NET type that is either a class or a struct
and ShapePoco<'Poco> private () =
    let isStruct = typeof<'Poco>.IsValueType
    let ctors =
        typeof<'Poco>.GetConstructors(allInstanceMembers)
        |> Array.map (fun c -> mkCtorUntyped<'Poco> c)

    let fields = 
        typeof<'Poco>.GetFields(allInstanceMembers)
        |> Array.map (fun f -> mkWriteMemberUntyped<'Poco> f.Name f [|f|])

    let properties =
        typeof<'Poco>.GetProperties(allInstanceMembers)
        |> Array.map (fun p -> mkMemberUntyped<'Poco> p.Name p [|p|])

    /// True iff POCO is a struct
    member __.IsStruct = isStruct
    /// Constructor shapes for the type
    member __.Constructors = ctors
    /// Field shapes for the type
    member __.Fields = fields
    /// Property shapes for the type
    member __.Properties = properties

    /// Creates an uninitialized instance for POCO
    member inline __.CreateUninitialized() : 'Poco = 
        FormatterServices.GetUninitializedObject(typeof<'Poco>) :?> 'Poco

    member inline __.CreateUninitializedExpr() : Expr<'Poco> =
        <@ FormatterServices.GetUninitializedObject(typeof<'Poco>) :?> 'Poco @>

    interface IShapePoco with
        member __.Constructors = ctors |> Array.map (fun c -> c :> _)
        member __.Fields = fields |> Array.map (fun f -> f :> _)
        member __.Properties = properties |> Array.map (fun p -> p :> _)
        member __.IsStruct = isStruct
        member __.Accept v = v.Visit __

and IPocoVisitor<'R> =
    abstract Visit : ShapePoco<'Poco> -> 'R

//--------------------------------------
// Section: TypeShape active recognizers

[<RequireQualifiedAccess>]
module Shape =

    let private SomeU = Some() // avoid allocating all the time
    let inline private test<'T> (s : TypeShape) =
        match s with
        | :? TypeShape<'T> -> SomeU
        | _ -> None

    // ----------
    // Primitives

    let (|Bool|_|) s = test<bool> s
    let (|Byte|_|) s = test<byte> s
    let (|SByte|_|) s = test<sbyte> s
    let (|Int16|_|) s = test<int16> s
    let (|Int32|_|) s = test<int32> s
    let (|Int64|_|) s = test<int64> s
    let (|IntPtr|_|) s = test<nativeint> s
    let (|UInt16|_|) s = test<uint16> s
    let (|UInt32|_|) s = test<uint32> s
    let (|UInt64|_|) s = test<uint64> s
    let (|UIntPtr|_|) s = test<unativeint> s
    let (|Single|_|) s = test<single> s
    let (|Double|_|) s = test<double> s
    let (|Char|_|) s = test<char> s

#if !TYPESHAPE_DISABLE_BIGINT
    let (|BigInt|_|) s = test<bigint> s
#endif

    let (|String|_|) s = test<string> s
    let (|Guid|_|) s = test<Guid> s
    let (|Decimal|_|) s = test<decimal> s
    let (|TimeSpan|_|) s = test<TimeSpan> s
    let (|DateTime|_|) s = test<DateTime> s
    let (|DateTimeOffset|_|) s = test<DateTimeOffset> s
    let (|Unit|_|) s = test<unit> s
    let (|FSharpUnit|_|) s = test<unit> s
    let (|ByteArray|_|) s = test<byte []> s
    
    /// Recognizes any type that is a System.Nullable instance
    let (|Nullable|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Nullable<_>> ->
            Activator.CreateInstanceGeneric<ShapeNullable<_>>(ta) 
            :?> IShapeNullable
            |> Some

        | _ -> None
        
    /// Recognizes any type that is a .NET enumeration
    let (|Enum|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Enum(e,u) ->
            Activator.CreateInstanceGeneric<ShapeEnum<BindingFlags, int>> [|e;u|]
            :?> IShapeEnum 
            |> Some
        | _ -> None

    /// Recognizes any type that satisfies the F# `equality` constraint
    let (|Equality|_|) (s : TypeShape) =
        // Since equality & comparison constraints are not contained
        // in reflection metadata, we need to separately determine 
        // whether they are satisfied
        // c.f. Section 5.2.10 of the F# Spec
        let rec isEqualityConstraint (stack:Type list) (t:Type) =
            if stack |> List.exists ((=) t) then true // recursive paths resolve to true always
            elif FSharpType.IsUnion(t, allMembers) then 
                if t.ContainsAttr<NoEqualityAttribute>(true) then false 
                elif t.ContainsAttr<CustomEqualityAttribute>(true) then true
                else
                    FSharpType.GetUnionCases(t, allMembers)
                    |> Seq.collect (fun uci -> uci.GetFields())
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isEqualityConstraint (t :: stack))

            elif t.ContainsAttr<NoEqualityAttribute>(false) then false
            elif FSharpType.IsRecord(t, allMembers) then
                if t.ContainsAttr<CustomEqualityAttribute>(true) then false
                else
                    FSharpType.GetRecordFields(t, allMembers)
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isEqualityConstraint (t :: stack))

            elif FSharpType.IsTuple t then
                FSharpType.GetTupleElements t
                |> Seq.distinct
                |> Seq.forall (isEqualityConstraint (t :: stack))

            elif FSharpType.IsFunction t then false
            elif t.IsArray then
                isEqualityConstraint (t :: stack) (t.GetElementType())
            else
                true

        if isEqualityConstraint [] s.Type then
            Activator.CreateInstanceGeneric<ShapeEquality<_>> [|s.Type|]
            :?> IShapeEquality
            |> Some
        else
            None

    /// Recognizes any type that satisfies the F# `comparison` constraint
    let (|Comparison|_|) (s : TypeShape) =
        // Since equality & comparison constraints are not contained
        // in reflection metadata, we need to separately determine 
        // whether they are satisfied
        // c.f. Section 5.2.10 of the F# Spec
        let rec isComparisonConstraint (stack:Type list) (t:Type) =
            if t = typeof<IntPtr> || t = typeof<UIntPtr> then true
            elif stack |> List.exists ((=) t) then true // recursive paths resolve to true always
            elif FSharpType.IsUnion(t, allMembers) then 
                if t.ContainsAttr<NoComparisonAttribute>(true) then false 
                elif t.ContainsAttr<CustomComparisonAttribute>(true) then true
                else
                    FSharpType.GetUnionCases(t, allMembers)
                    |> Seq.collect (fun uci -> uci.GetFields())
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isComparisonConstraint (t :: stack))

            elif t.ContainsAttr<NoComparisonAttribute>(false) then false
            elif FSharpType.IsRecord(t, allMembers) then
                if t.ContainsAttr<CustomComparisonAttribute>(true) then false
                else
                    FSharpType.GetRecordFields(t, allMembers)
                    |> Seq.map (fun p -> p.PropertyType)
                    |> Seq.distinct
                    |> Seq.forall (isComparisonConstraint (t :: stack))

            elif FSharpType.IsTuple t then
                FSharpType.GetTupleElements t
                |> Seq.distinct
                |> Seq.forall (isComparisonConstraint (t :: stack))

            elif t.IsArray then
                isComparisonConstraint (t :: stack) (t.GetElementType())

            elif isInterfaceAssignableFrom typeof<IComparable> t then true
            else
                false

        if isComparisonConstraint [] s.Type then
            Activator.CreateInstanceGeneric<ShapeComparison<_>> [|s.Type|]
            :?> IShapeComparison
            |> Some
        else
            None

    /// Identifies whether shape satisfies the 'struct' or 'not struct' constraint
    let (|Struct|NotStruct|) (s : TypeShape) =
        if s.Type.IsValueType then
            let instance = Activator.CreateInstanceGeneric<ShapeStruct<_>> [|s.Type|] :?> IShapeStruct
            Struct instance
        else
            let instance = Activator.CreateInstanceGeneric<ShapeNotStruct<_>> [|s.Type|] :?> IShapeNotStruct
            NotStruct instance

    /// Recognizes shapes that carry a parameterless constructor
    let (|DefaultConstructor|_|) (shape : TypeShape) =
        match shape.Type.GetConstructor(BindingFlags.Public ||| BindingFlags.Instance, null, [||], [||]) with
        | null -> None
        | _ -> 
            Activator.CreateInstanceGeneric<ShapeDefaultConstructor<_>>([|shape.Type|]) 
            :?> IShapeDefaultConstructor
            |> Some

    /// Recognizes shapes that are instances of System.Collections.Generic.KeyValuePair<_,_>
    let (|KeyValuePair|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<KeyValuePair<_,_>> ->
            Activator.CreateInstanceGeneric<ShapeKeyValuePair<_,_>>(ta)
            :?> IShapeKeyValuePair
            |> Some
        | _ ->
            None

    /// Recognizes shapes that are instances of System.Collections.Generic.Dictionary<_,_>
    let (|Dictionary|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Dictionary<_,_>> ->
            Activator.CreateInstanceGeneric<ShapeDictionary<_,_>>(ta)
            :?> IShapeDictionary
            |> Some
        | _ ->
            None

    /// Recognizes shapes that are instances of System.Collections.Generic.HashSet<_>
    let (|HashSet|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<HashSet<_>> ->
            Activator.CreateInstanceGeneric<ShapeHashSet<_>>(ta)
            :?> IShapeHashSet
            |> Some
        | _ ->
            None

    /// Recognizes shapes that are instances of System.Collections.Generic.List<_>
    let (|ResizeArray|_|) (s : TypeShape) = 
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<ResizeArray<_>> ->
            Activator.CreateInstanceGeneric<ShapeResizeArray<_>>(ta)
            :?> IShapeResizeArray
            |> Some
        | _ ->
            None

    /// Recognizes shapes that inherit from System.Delegate
    let (|Delegate|_|) (s : TypeShape) =
        if typeof<System.Delegate>.IsAssignableFrom s.Type then
            Activator.CreateInstanceGeneric<ShapeDelegate<_>>([|s.Type|])
            :?> IShapeDelegate
            |> Some
        else
            None

    /// Recognozes shapes that inherit from System.Exception
    let (|Exception|_|) (s : TypeShape) =
        if typeof<System.Exception>.IsAssignableFrom s.Type then
            let isFSharpExn = FSharpType.IsExceptionRepresentation(s.Type, allMembers)
            Activator.CreateInstanceGeneric<ShapeException<_>>([|s.Type|], [|isFSharpExn|])
            :?> IShapeException
            |> Some
        else
            None

    /// Recognizes shapes that are .NET arrays
    let (|Array|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | TypeShapeInfo.Array(et,rk) ->
            Activator.CreateInstanceGeneric<ShapeArray<_>>([|et|], [|box rk|])
            :?> IShapeArray
            |> Some
        | _ ->
            None

    /// Recognizes instances of System.Tuple<_>
    let (|Tuple1|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Tuple<_>> ->
            Activator.CreateInstanceGeneric<ShapeTuple1<_>>(ta)
            :?> IShapeTuple1
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_>
    let (|Tuple2|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple2<_,_>>(ta)
            :?> IShapeTuple2
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_>
    let (|Tuple3|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple3<_,_,_>>(ta)
            :?> IShapeTuple3
            |> Some
        | _ -> None
        
    /// Recognizes instances of System.Tuple<_,_,_,_>
    let (|Tuple4|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple4<_,_,_,_>>(ta)
            :?> IShapeTuple4
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_>
    let (|Tuple5|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple5<_,_,_,_,_>>(ta)
            :?> IShapeTuple5
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_,_>
    let (|Tuple6|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple6<_,_,_,_,_,_>>(ta)
            :?> IShapeTuple6
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_,_,_>
    let (|Tuple7|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ * _ * _ * _ * _ * _ * _> ->
            Activator.CreateInstanceGeneric<ShapeTuple7<_,_,_,_,_,_,_>>(ta)
            :?> IShapeTuple7
            |> Some
        | _ -> None

    /// Recognizes instances of System.Tuple<_,_,_,_,_,_,_,_>
    let (|Tuple8|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Tuple<_,_,_,_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeTuple8<_,_,_,_,_,_,_,_>>(ta)
            :?> IShapeTuple8
            |> Some
        | _ -> None

    /// Recognizes shapes of F# list types
    let (|FSharpList|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ list> ->
            Activator.CreateInstanceGeneric<ShapeFSharpList<_>>(ta)
            :?> IShapeFSharpList
            |> Some
        | _ -> None

    /// Recognizes shapes of F# option types
    let (|FSharpOption|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ option> ->
            Activator.CreateInstanceGeneric<ShapeFSharpOption<_>>(ta)
            :?> IShapeFSharpOption
            |> Some
        | _ -> None

    /// Recognizes shapes of F# ref types
    let (|FSharpRef|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<_ ref> ->
            Activator.CreateInstanceGeneric<ShapeFSharpRef<_>>(ta)
            :?> IShapeFSharpRef
            |> Some
        | _ -> None

    /// Recognizes shapes of F# set types
    let (|FSharpSet|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Set<_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpSet<_>>(ta)
            :?> IShapeFSharpSet
            |> Some
        | _ -> None

    /// Recognizes shapes of F# map types
    let (|FSharpMap|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Map<_,_>> -> 
            Activator.CreateInstanceGeneric<ShapeFSharpMap<_,_>>(ta)
            :?> IShapeFSharpMap
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_> types
    let (|FSharpChoice2|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_>>(ta)
            :?> IShapeFSharpChoice2
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_> types
    let (|FSharpChoice3|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_>>(ta)
            :?> IShapeFSharpChoice3
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_> types
    let (|FSharpChoice4|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_>>(ta)
            :?> IShapeFSharpChoice4
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_,_> types
    let (|FSharpChoice5|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_,_>>(ta)
            :?> IShapeFSharpChoice5
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_,_,_> types
    let (|FSharpChoice6|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_,_,_>>(ta)
            :?> IShapeFSharpChoice6
            |> Some
        | _ -> None

    /// Recognizes shapes of F# Choice<_,_,_,_,_,_,_> types
    let (|FSharpChoice7|_|) (s : TypeShape) =
        match s.ShapeInfo with
        | Generic(td,ta) when td = typedefof<Choice<_,_,_,_,_,_,_>> ->
            Activator.CreateInstanceGeneric<ShapeFSharpChoice<_,_,_,_,_,_,_>>(ta)
            :?> IShapeFSharpChoice7
            |> Some
        | _ -> None

    /// Recognizes shapes of F# function types
    let (|FSharpFunc|_|) (s : TypeShape) =
        if FSharpType.IsFunction s.Type then
            let d,c = FSharpType.GetFunctionElements s.Type
            Activator.CreateInstanceGeneric<ShapeFSharpFunc<_,_>> [|d;c|]
            :?> IShapeFSharpFunc
            |> Some
        else None

    /// Recognizes shapes that implement System.Collections.Generic.ICollection<_>
    let (|Collection|_|) (s : TypeShape) =
        match s.Type.GetInterface("ICollection`1") with
        | null ->
            match s.ShapeInfo with
            | Generic(td,ta) when td = typedefof<ICollection<_>> ->
                Activator.CreateInstanceGeneric<ShapeCollection<_,_>> [|s.Type; ta.[0]|]
                :?> IShapeCollection
                |> Some
            | _ -> None
        | iface ->
            let args = iface.GetGenericArguments()
            Activator.CreateInstanceGeneric<ShapeCollection<_,_>> [|s.Type; args.[0]|]
            :?> IShapeCollection
            |> Some

    /// Recognizes shapes that implement System.Collections.Generic.IEnumerable<_>
    let (|Enumerable|_|) (s : TypeShape) =
        match s.Type.GetInterface("IEnumerable`1") with
        | null ->
            match s.ShapeInfo with
            | Generic(td,ta) when td = typedefof<IEnumerable<_>> ->
                Activator.CreateInstanceGeneric<ShapeEnumerable<_,_>> [|s.Type; ta.[0]|]
                :?> IShapeEnumerable
                |> Some
            | _ -> None
        | iface ->
            let args = iface.GetGenericArguments()
            Activator.CreateInstanceGeneric<ShapeEnumerable<_,_>> [|s.Type; args.[0]|]
            :?> IShapeEnumerable
            |> Some

    /// Recognizes shapes that are F# records
    let (|FSharpRecord|_|) (s : TypeShape) =
        if FSharpType.IsRecord(s.Type, allMembers) then
            Activator.CreateInstanceGeneric<ShapeFSharpRecord<_>>([|s.Type|], [||])
            :?> IShapeFSharpRecord
            |> Some
        else
            None

    /// Recognizes shapes that are F# unions
    let (|FSharpUnion|_|) (s : TypeShape) =
        if FSharpType.IsUnion(s.Type, allMembers) then
            Activator.CreateInstanceGeneric<ShapeFSharpUnion<_>>([|s.Type|], [||])
            :?> IShapeFSharpUnion
            |> Some
        else
            None

    /// Recognizes shapes that are System.Tuple instances of arbitrary arity
    let (|Tuple|_|) (s : TypeShape) =
        if FSharpType.IsTuple s.Type then
            Activator.CreateInstanceGeneric<ShapeTuple<_>>([|s.Type|], [||])
            :?> IShapeTuple
            |> Some
        else
            None

    /// Recognizes shapes that look like C# record classes
    /// They are classes with parameterless constructors and settable properties
    let (|CliMutable|_|) (s : TypeShape) =
        match s.Type.GetConstructor(BindingFlags.Public ||| BindingFlags.Instance, null, [||], [||]) with
        | null -> None
        | ctor -> 
            Activator.CreateInstanceGeneric<ShapeCliMutable<_>>([|s.Type|], [|ctor|])
            :?> IShapeCliMutable
            |> Some

    /// Recognizes POCO shapes, .NET types that are either classes or structs
    let (|Poco|_|) (s : TypeShape) =
        if s.Type.IsClass || s.Type.IsValueType then
            Activator.CreateInstanceGeneric<ShapePoco<_>>([|s.Type|], [||])
            :?> IShapePoco
            |> Some
        else
            None