-- Create an item, keeping shop:item-names unique (data-model.md "Redis layout").
-- KEYS[1] shop:items  KEYS[2] shop:item-names
-- ARGV[1] id  ARGV[2] nameKey  ARGV[3] item JSON
if redis.call('HSETNX', KEYS[2], ARGV[2], ARGV[1]) == 0 then
  return 'exists'
end
redis.call('HSET', KEYS[1], ARGV[1], ARGV[3])
return 'ok'
