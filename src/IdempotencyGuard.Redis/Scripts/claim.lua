-- @key = idempotency:{key}
-- @fingerprint = fingerprint
-- @claim_ttl_ms = claim_ttl_ms
-- @current_timestamp = current_timestamp
-- @claim_token = token identifying this hold of the claim

-- Lua scripts run atomically, so nothing can claim the key between these two calls.
local existing = redis.call('GET', @key)
if existing then
    return existing
end

local entry = cjson.encode({
    state = 'claimed',
    fingerprint = @fingerprint,
    claimed_at = @current_timestamp,
    claim_token = @claim_token
})

redis.call('SET', @key, entry, 'PX', @claim_ttl_ms)
return nil
