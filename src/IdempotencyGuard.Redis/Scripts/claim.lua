-- KEYS[1] = idempotency:{key}
-- ARGV[1] = fingerprint
-- ARGV[2] = claim_ttl_ms
-- ARGV[3] = current_timestamp

local existing = redis.call('GET', KEYS[1])
if existing then
    return existing
end

local entry = cjson.encode({
    state = 'claimed',
    fingerprint = ARGV[1],
    claimed_at = ARGV[3]
})

redis.call('SET', KEYS[1], entry, 'PX', ARGV[2], 'NX')

-- Check if we actually set it (another client might have beaten us)
local check = redis.call('GET', KEYS[1])
if check then
    local decoded = cjson.decode(check)
    if decoded.fingerprint == ARGV[1] and decoded.claimed_at == ARGV[3] then
        return nil -- Claim succeeded
    end
    return check -- Someone else claimed it
end

return nil
