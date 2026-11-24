-- Atomic order placement (research R9): check every line, then decrement stock and store the
-- order, or change nothing and return the problems.
-- KEYS[1] shop:items  KEYS[2] shop:order:{reference}  KEYS[3] shop:orders
-- ARGV[1] reference  ARGV[2] createdAt (ms)  ARGV[3] walletAddress  ARGV[4] status
-- ARGV[5] totalCents (computed exactly by the app)  ARGV[6..] itemId, quantity, unitPriceCents
if redis.call('EXISTS', KEYS[2]) == 1 then
  return cjson.encode({taken = true})
end

local found, problems, lines = {}, {}, {}
for i = 6, #ARGV, 3 do
  local id, qty, price = ARGV[i], tonumber(ARGV[i + 1]), tonumber(ARGV[i + 2])
  local raw = redis.call('HGET', KEYS[1], id)
  if not raw then
    problems[#problems + 1] = {itemId = id, kind = 'removed'}
  else
    local item = cjson.decode(raw)
    if item.priceCents ~= price then
      problems[#problems + 1] = {itemId = id, kind = 'price', currentPriceCents = item.priceCents,
                                 currentStock = item.stock}
    elseif item.stock < qty then
      problems[#problems + 1] = {itemId = id, kind = 'stock', currentStock = item.stock}
    else
      found[#found + 1] = {id = id, item = item, qty = qty}
      lines[#lines + 1] = {itemId = id, name = item.name, unitPriceCents = price, quantity = qty}
    end
  end
end

if #problems > 0 then
  return cjson.encode({problems = problems})
end

local createdAt = tonumber(ARGV[2])
for _, e in ipairs(found) do
  e.item.stock = e.item.stock - e.qty
  e.item.updatedAt = createdAt
  redis.call('HSET', KEYS[1], e.id, cjson.encode(e.item))
end

-- totalCents is spliced in as text so it stays exact whatever its size.
local order = '{"reference":' .. cjson.encode(ARGV[1]) .. ',"createdAt":' .. ARGV[2] ..
  ',"status":' .. cjson.encode(ARGV[4]) .. ',"walletAddress":' .. cjson.encode(ARGV[3]) ..
  ',"totalCents":' .. ARGV[5] .. ',"lines":' .. cjson.encode(lines) .. '}'
redis.call('SET', KEYS[2], order)
redis.call('ZADD', KEYS[3], ARGV[2], ARGV[1])
return order
