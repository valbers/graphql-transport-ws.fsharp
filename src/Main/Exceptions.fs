namespace GraphQLTransportWS

type InvalidMessageException (explanation : string) =
  inherit System.Exception(explanation)