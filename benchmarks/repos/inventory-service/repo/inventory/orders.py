from inventory.config import MAX_BATCH
from inventory.pricing import total_price
from inventory.stock import add_stock, remove_stock


def place_order(item, qty, unit_price):
    if qty > MAX_BATCH:
        raise ValueError("order too large")
    remove_stock(item, qty)
    return total_price(unit_price, qty)


def cancel_order(item, qty):
    # undo place_order; no refund calculation here
    return add_stock(item, qty)
