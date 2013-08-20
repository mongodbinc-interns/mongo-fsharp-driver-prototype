namespace FSharp.MongoDB.Driver

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Reflection

open MongoDB.Bson

open MongoDB.Driver.Core

open FSharp.MongoDB.Driver.Quotations

module Expression =

    type IMongoQueryable<'a> =
        inherit seq<'a>

    type IMongoUpdatable<'a> =
        inherit seq<'a>

    type IMongoDeferrable<'a> =
        inherit seq<'a>

    [<RequireQualifiedAccess>]
    type private TransformResult = // (fun * args * cont)
       | Query of (Var -> Expr -> BsonElement) * (Var * Expr) * Expr
       | Update of (Var -> string -> Expr -> BsonElement) * (Var * string * Expr) * Expr

    [<RequireQualifiedAccess>]
    type private TraverseResult = // (fun * args)
       | Query of (Var -> Expr -> BsonElement) * (Var * Expr)
       | Update of (Var -> string -> Expr -> BsonElement) * (Var * string * Expr)

    type MongoDeferredOperation =
       | Query of BsonDocument
       | Update of BsonDocument * BsonDocument

    [<RequireQualifiedAccess>]
    type MongoOperationResult<'a> =
       | Query of Scope<'a>
       | Update of WriteConcernResult
       | Deferred of MongoDeferredOperation

    type MongoBuilder() =

        let mkCall op args =
            match <@ ignore %op @> with
            | SpecificCall <@ ignore @> (_, _, [ Lambdas (_, Call (target, info, _)) ]) ->
                match target with
                | Some x -> Expr.Call(x, info, args)
                | None -> Expr.Call(info, args)
            | _ -> failwith "unable to acquire methodinfo"

        let rec (|List|_|) expr =
            match expr with
            | NewUnionCase (uci, args) when isListUnionCase uci ->
                match args with
                | [] -> Some([], typeof<unit>)
                | [ head; List (tail, _) ] -> Some(head :: tail, typeof<Expr>)
                | _ -> failwith "unexpected list union case"

            | _ -> None

        let (|ValueOrList|_|) expr =
            match expr with
            | Value (value, typ) -> Some (value, typ)
            | List (values, typ) -> Some (box values, typ)
            | _ -> None

        let transform (x : MongoBuilder) expr =
            match expr with
            // Query operations
            | SpecificCall <@ x.Where @> (_, _, [ cont; Lambda (var, body) ]) ->
                let res = TransformResult.Query (queryParser, (var, body), cont)
                Some res

            // Update operations
            | SpecificCall <@ x.Inc @> (_, _, [ cont; Lambda (_, body); Int32 (value) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, Expr.Coerce(body, typeof<int>))
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let call = mkCall <@ (+) @> [ body; Expr.Value value ]
                let res = TransformResult.Update (updateParser, (var, field, call), cont)
                Some res

            | SpecificCall <@ x.Dec @> (_, _, [ cont; Lambda (_, body); Int32 (value) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, Expr.Coerce(body, typeof<int>))
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let call = mkCall <@ (-) @> [ body; Expr.Value value ]
                let res = TransformResult.Update (updateParser, (var, field, call), cont)
                Some res

            | SpecificCall <@ x.Set @> (_, _, [ cont; Lambda (_, body); ValueOrList (value, _) ]) ->
                match body with
                | GetField (var, field) ->
                    let res = TransformResult.Update (updateParser, (var, field, Expr.Value value), cont)
                    Some res
                | _ -> failwithf "unrecognized expression\n%A" body

            | SpecificCall <@ x.Unset @> (_, _, [ cont; Lambda (_, body) ]) ->
                match body with
                | GetField (var, field) ->
                    let cases =
                        FSharpType.GetUnionCases(typeof<option<_>>)
                        |> Seq.map (fun x -> (x.Name, x))
                        |> dict
                    let res = TransformResult.Update (updateParser, (var, field, Expr.NewUnionCase(cases.["None"], []) ), cont)
                    Some res
                | _ -> failwithf "unrecognized expression\n%A" body

            | _ -> None

        let traverse (x : MongoBuilder) expr =
            let rec traverse builder expr res =
                match transform builder expr with
                | Some trans ->
                    match trans with
                    | TransformResult.Query (f, args, cont) ->
                        let x = TraverseResult.Query (f, args)
                        traverse builder cont (x :: res)

                    | TransformResult.Update (f, args, cont) ->
                        let x = TraverseResult.Update (f, args)
                        traverse builder cont (x :: res)

                | None -> res

            traverse x expr []

        member __.Source (x : #seq<'a>) : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.For (source : IMongoQueryable<'a>, f : 'a -> IMongoQueryable<'a>) = invalidOp "not implemented"

        member __.Zero () : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.Yield x : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.Quote (expr : Expr<#seq<'a>>) = expr

        member x.Run (expr : Expr<#seq<'a>>) : MongoOperationResult<'a> =

            let isUpdate = ref false

            let queryDoc = BsonDocument()
            let updateDoc = BsonDocument()

            let prepareDocs expr =
                traverse x expr
                |> List.iter (fun x ->
                    match x with
                    | TraverseResult.Query (f, x) ->
                        let elem = x ||> f
                        queryDoc.Add elem |> ignore

                    | TraverseResult.Update (f, x) ->
                        isUpdate := true

                        let elem = x |||> f
                        updateDoc.Add elem |> ignore
                    )

            match expr with
            | SpecificCall <@ x.Defer @> (_, _, [ expr ]) ->
                prepareDocs expr

                let res =
                    if !isUpdate then // dereference
                        MongoDeferredOperation.Update (queryDoc, updateDoc)
                    else
                        MongoDeferredOperation.Query queryDoc

                MongoOperationResult.Deferred res

            | _ -> invalidOp "not implemented yet"

        [<CustomOperation("defer")>]
        member __.Defer (source : #seq<'a>) : IMongoDeferrable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("where", MaintainsVariableSpace = true)>]
        member __.Where (source : IMongoQueryable<'a>, [<ProjectionParameter>] predicate : 'a -> bool) : IMongoQueryable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("update", MaintainsVariableSpace = true)>]
        member __.Update (source : IMongoQueryable<'a>) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("inc", MaintainsVariableSpace = true)>]
        member __.Inc (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("dec", MaintainsVariableSpace = true)>]
        member __.Dec (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("set", MaintainsVariableSpace = true)>]
        member __.Set (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("unset", MaintainsVariableSpace = true)>]
        member __.Unset (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("addToSet", MaintainsVariableSpace = true)>]
        member __.AddToSet (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("popLeft", MaintainsVariableSpace = true)>]
        member __.PopLeft (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("popRight", MaintainsVariableSpace = true)>]
        member __.PopRight (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("pull", MaintainsVariableSpace = true)>]
        member __.Pull (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, predicate : 'b -> bool) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("pullAll", MaintainsVariableSpace = true)>]
        member __.PullAll (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, values : 'b list) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("push", MaintainsVariableSpace = true)>]
        member __.Push (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("bitAnd", MaintainsVariableSpace = true)>]
        member __.BitAnd (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("bitOr", MaintainsVariableSpace = true)>]
        member __.BitOr (source : IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

    let mongo = new MongoBuilder()