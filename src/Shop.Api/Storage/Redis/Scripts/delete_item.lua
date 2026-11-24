-- Delete an item and its name entry. Orders are never touched (FR-003).
-- KEYS[1] shop:items  KEYS[2] shop:item-names  ARGV[1] id
local raw = redis.call('HGET', KEYS[1], ARGV[1])
if not raw then
  return 0
end
local item = cjson.decode(raw)
redis.call('HDEL', KEYS[1], ARGV[1])
if redis.call('HGET', KEYS[2], item.nameKey) == ARGV[1] then
  redis.call('HDEL', KEYS[2], item.nameKey)
end
return 1
