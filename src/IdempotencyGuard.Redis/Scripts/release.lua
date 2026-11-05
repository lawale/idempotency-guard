-- @key = idempotency:{key}
-- @claim_token = token returned by the claim

local existing = redis.call('GET', @key)
if not existing then
    return 0
end

local claimed = cjson.decode(existing)
if claimed.state ~= 'claimed' or claimed.claim_token ~= @claim_token then
    return 0
end

redis.call('DEL', @key)
return 1
