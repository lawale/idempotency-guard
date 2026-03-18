-- @key = idempotency:{key}
-- @fingerprint = fingerprint
-- @claim_ttl_ms = claim_ttl_ms
-- @current_timestamp = current_timestamp

local existing = redis.call('GET', @key)
if existing then
    return existing
end

local entry = cjson.encode({
    state = 'claimed',
    fingerprint = @fingerprint,
    claimed_at = @current_timestamp
})

redis.call('SET', @key, entry, 'PX', @claim_ttl_ms, 'NX')

-- Check if we actually set it (another client might have beaten us)
local check = redis.call('GET', @key)
if check then
    local decoded = cjson.decode(check)
    if decoded.fingerprint == @fingerprint and decoded.claimed_at == @current_timestamp then
        return nil -- Claim succeeded
    end
    return check -- Someone else claimed it
end

return nil
