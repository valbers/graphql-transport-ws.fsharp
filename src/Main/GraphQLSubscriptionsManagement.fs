namespace GraphQLTransportWS

module internal GraphQLSubscriptionsManagement =
  let addSubscription (id : SubscriptionId, unsubscriber : SubscriptionUnsubscriber, onUnsubscribe : OnUnsubscribeAction)
                      (subscriptions : SubscriptionsDict) =
    printfn "GraphQLSubscriptionsManagement: new subscription (id: \"%s\". Total: %d)" (id |> string) subscriptions.Count
    subscriptions.Add(id, (unsubscriber, onUnsubscribe))

  let isIdTaken (id : SubscriptionId) (subscriptions : SubscriptionsDict) =
    subscriptions.ContainsKey(id)

  let executeOnUnsubscribeAndDispose (id : SubscriptionId) (subscription : SubscriptionUnsubscriber * OnUnsubscribeAction) =
      match subscription with
      | unsubscriber, onUnsubscribe ->
        try
          id |> onUnsubscribe
        finally
          unsubscriber.Dispose()

  let removeSubscription (id: SubscriptionId) (subscriptions : SubscriptionsDict) =
    if subscriptions.ContainsKey(id) then
      subscriptions.[id]
      |> executeOnUnsubscribeAndDispose id
      subscriptions.Remove(id) |> ignore

  let removeAllSubscriptions (subscriptions : SubscriptionsDict) =
    subscriptions
    |> Seq.iter
        (fun subscription ->
            subscription.Value
            |> executeOnUnsubscribeAndDispose subscription.Key
        )
    subscriptions.Clear()