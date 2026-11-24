-- Update an item; a rename moves its shop:item-names entry.
-- KEYS[1] shop:items  KEYS[2] shop:item-names
-- ARGV[1] id  ARGV[2] name  ARGV[3] nameKey  ARGV[4] stock  ARGV[5] priceCents  ARGV[6] updatedAt (ms)
local raw = redis.call('HGET', KEYS[1], ARGV[1])
if not raw then
  return {'notfound'}
end
local item = cjson.decode(raw)
if item.nameKey ~= ARGV[3] then
  local owner = redis.call('HGET', KEYS[2], ARGV[3])
  if owner and owner ~= ARGV[1] then
    return {'exists'}
  end
  redis.call('HSET', KEYS[2], ARGV[3], ARGV[1])
  if redis.call('HGET', KEYS[2], item.nameKey) == ARGV[1] then
    redis.call('HDEL', KEYS[2], item.nameKey)
  end
end
item.name = ARGV[2]
item.nameKey = ARGV[3]
item.stock = tonumber(ARGV[4])
item.priceCents = tonumber(ARGV[5])
item.updatedAt = tonumber(ARGV[6])
local json = cjson.encode(item)
redis.call('HSET', KEYS[1], ARGV[1], json)
return {'ok', json}
