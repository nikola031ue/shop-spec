-- Expires due orders atomically (spec 005 research R5): waiting, past the deadline, and the chain
-- progress of the order's address past the deadline too. Each one: status Expired, stock back to
-- items that still exist, waiting indexes cleared. Exactly once (the script runs alone).
-- KEYS[1] shop:waiting  KEYS[2] shop:items  KEYS[3] shop:chain-progress
-- ARGV[1] now (ms)  ARGV[2] limit
-- Returns the number of orders expired.
local now, limit = tonumber(ARGV[1]), tonumber(ARGV[2])
local expired = 0
local candidates = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '(' .. ARGV[1], 'LIMIT', 0, limit * 4)
for _, reference in ipairs(candidates) do
  if expired >= limit then
    break
  end
  local raw = redis.call('GET', 'shop:order:' .. reference)
  if not raw then
    redis.call('ZREM', KEYS[1], reference)
  else
    local o = cjson.decode(raw)
    local status = redis.call('HGET', 'shop:order-state:' .. reference, 'status') or o.status
    local deadline = o.deadline or (o.createdAt + 30 * 60 * 1000)
    local progress = redis.call('HGET', KEYS[3], o.walletAddress)
    local seenTime = progress and tonumber(string.match(progress, '|(%d+)$'))
    if status ~= 'PendingPayment' then
      redis.call('ZREM', KEYS[1], reference)
    elseif deadline < now and seenTime and deadline < seenTime then
      for _, line in ipairs(o.lines) do
        local itemRaw = redis.call('HGET', KEYS[2], line.itemId)
        if itemRaw then
          local item = cjson.decode(itemRaw)
          item.stock = item.stock + line.quantity
          item.updatedAt = now
          redis.call('HSET', KEYS[2], line.itemId, cjson.encode(item))
        end
      end
      redis.call('HSET', 'shop:order-state:' .. reference, 'status', 'Expired')
      redis.call('ZREM', KEYS[1], reference)
      redis.call('SREM', 'shop:waiting-address:' .. o.walletAddress, reference)
      if o.payingAccount then
        local key = 'shop:waiting-account:' .. o.payingAccount
        if redis.call('GET', key) == reference then
          redis.call('DEL', key)
        end
      end
      expired = expired + 1
    end
  end
end
return expired
