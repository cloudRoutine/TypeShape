#r "../bin/TypeShape.dll"
open System
open TypeShape

// inspired by but different from http://research.microsoft.com/en-us/um/people/simonpj/papers/hmap/gmap3.pdf

let rec gmapQ<'T,'S,'U> (f : 'T -> 'S) : 'U -> 'S list =
    let wrap(f : 'a -> 'S list) = unbox<'U -> 'S list> f

    let gmapQMember (shape : IShapeMember<'a>) =
        shape.Accept { new IMemberVisitor<'a, 'a -> 'S list> with
            member __.Visit (shape : ShapeMember<'a, 'f>) =
                gmapQ<'T, 'S, 'f> f << shape.Project }

    match shapeof<'U> :> TypeShape with
    | :? TypeShape<'T> -> wrap(fun (t:'T) -> [f t])
    | Shape.FSharpOption s ->
        s.Accept {
            new IFSharpOptionVisitor<'U -> 'S list> with
                member __.Visit<'a>() =
                    let tm = gmapQ<'T,'S,'a> f
                    wrap(function None -> [] | Some t -> tm t)
        }

    | Shape.Array s when s.Rank = 1 ->
        s.Accept {
            new IArrayVisitor<'U -> 'S list> with
                member __.Visit<'a> _ =
                    let tm = gmapQ<'T, 'S, 'a> f
                    wrap(fun (ts : 'a []) -> ts |> Seq.collect tm |> Seq.toList)
        }

    | Shape.FSharpList s ->
        s.Accept {
            new IFSharpListVisitor<'U -> 'S list> with
                member __.Visit<'a>() =
                    let tm = gmapQ<'T,'S,'a> f
                    wrap(fun (ts : 'a list) -> ts |> List.collect tm)
        }

    | Shape.FSharpSet s ->
        s.Accept {
            new IFSharpSetVisitor<'U -> 'S list> with
                member __.Visit<'a when 'a : comparison> () =
                    let tm = gmapQ<'T,'S,'a> f
                    wrap(fun (ts : Set<'a>) -> ts |> Seq.collect tm |> Seq.toList)
        }

    | Shape.Tuple (:? ShapeTuple<'U> as shape) ->
        let eMappers = shape.Elements |> Seq.map gmapQMember |> Seq.toList
        fun (u:'U) -> eMappers |> List.collect (fun m -> m u)

    | Shape.FSharpRecord (:? ShapeFSharpRecord<'U> as shape) ->
        let fMappers = shape.Fields |> Seq.map gmapQMember |> Seq.toList
        fun (u:'U) -> fMappers |> List.collect (fun m -> m u)

    | Shape.FSharpUnion (:? ShapeFSharpUnion<'U> as shape) ->
        let umappers = 
            shape.UnionCases 
            |> Array.map (fun uc -> uc.Fields |> Seq.map gmapQMember |> Seq.toList)

        fun (u:'U) ->
            let tag = shape.GetTag u
            umappers.[tag] |> List.collect (fun m -> m u)

    | s -> 
        s.Accept { new ITypeShapeVisitor<'U -> 'S list> with
            member __.Visit<'a>() = wrap(fun (_:'a) -> []) }


// examples

gmapQ id<int> (Some 42, ([1 .. 10], set[5;6;7]))
gmapQ id<string> (Some 42, ([1 .. 10], "42", set[5;6;7]))


type Foo = A | B of int | C of int * string
type Bar = { Foo : Foo ; A : int ; B : string }

let value = { Foo = C(2,"2") ; A = 1 ; B = "1" }
gmapQ id<int> value
gmapQ id<string> value