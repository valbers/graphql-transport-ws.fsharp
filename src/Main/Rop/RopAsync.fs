module GraphQLTransportWS.RopAsync

open System.Threading.Tasks
open Rop


let bindToAsyncTaskR<'a, 'b, 'c> (f: 'a -> Task<RopResult<'b, 'c>>) result =
    task {
        let secondResult =
            match result with
            | Success (x, msgs) ->
              task {
                  let! secRes = f x
                  return secRes |> mergeMessages msgs
              }
            | Failure errs ->
              task {
                  return Failure errs
              }
        return! secondResult
    }