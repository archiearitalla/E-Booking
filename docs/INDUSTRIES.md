# VeskaFlow industries

The industry a shop picks at sign-up only chooses **wording** (what the booking page calls the person being booked) and the **starter service**. It never changes the booking rules. Source: `src/VeskaFlow.Web/Services/Industries.cs`, labels in `src/VeskaFlow.Web/Text.cs`, allowed keys in migration `020_more_industries.sql`.

Durations are in minutes. A number after "d" is the follow-up reminder in days. Prices start at 0 until the owner sets them.

## Summary

| Key | Industry | Starter service | Staff label |
|---|---|---|---|
| barbershop | Barbershop | Haircut | Barber |
| salon | Salon | Haircut and styling | Stylist |
| clinic | Clinic | Consultation | Doctor |
| spa | Spa and massage | Massage | Therapist |
| nails | Nail and lash studio | Manicure | Technician |
| pet | Pet grooming and vet | Grooming | Groomer or vet |
| fitness | Gym, trainer or yoga | Personal training | Trainer |
| tattoo | Tattoo and piercing | Design consultation | Artist |
| auto | Car wash and auto care | Car wash | Technician |
| tutor | Tutoring and lessons | One-hour lesson | Teacher |
| photo | Photo studio | Portrait session | Photographer |

The exact starter and staff wording for barbershop, salon and clinic is set in `Text.cs` and may differ slightly from the table above.

## Suggested services

Shown under "Add suggested services" on the Services page.

### Barbershop
Haircut 30, Haircut and beard trim 45, Beard trim 20, Shave 30, Kids haircut 30, Hair color 90, Hair wash 15, Hair and scalp treatment 45.

### Salon
Haircut and styling 45, Hair color 120, Rebond 180, Hair treatment 60, Manicure 45, Pedicure 60, Facial 60, Massage 60.

### Clinic
Consultation 30 (d14), Follow-up check-up 20, Dental cleaning 45 (d180), Tooth extraction 45 (d7), Dental filling 60, Braces adjustment 30 (d30), Teeth whitening 60, Medical certificate 15.

### Spa and massage
Swedish massage 60, Shiatsu massage 60, Hot stone massage 90, Back and shoulder massage 30, Foot reflexology 45, Body scrub 60, Facial 60, Aromatherapy 60.

### Nail and lash studio
Manicure 45, Pedicure 60, Gel polish 60, Nail extensions 90, Nail art 30, Eyelash extensions 90, Lash lift 60, Eyebrow shaping 30.

### Pet grooming and vet
Bath and blow dry 60, Full grooming 90, Nail trim 15, Ear cleaning 15, Vet consultation 30 (d14), Vaccination 20 (d21), Deworming 15 (d90), Pet check-up 30.

### Gym, trainer or yoga
Personal training 60, Group class 60, Yoga class 60, Dance fitness class 60, Fitness assessment 45, Nutrition consultation 30 (d14), Trial session 30, Stretching and recovery 30.

### Tattoo and piercing
Design consultation 30, Small tattoo 60 (d30), Medium tattoo 180 (d30), Large tattoo session 240 (d30), Piercing 30 (d14), Touch-up 60, Tattoo cover-up consultation 30, Tattoo removal consultation 30.

### Car wash and auto care
Car wash 30, Interior cleaning 60, Car detailing 180, Motorcycle wash 20, Oil change 45 (d90), Tire rotation 30, Aircon cleaning 60, General check-up 45.

### Tutoring and lessons
One-hour lesson 60, Trial lesson 30, Review session 90, Music lesson 45, Language lesson 60, Exam coaching 90, Parent meeting 20, Assessment 45.

### Photo studio
Portrait session 60, Family photoshoot 90, ID and passport photos 15, Graduation photos 60, Product photography 120, Couple or prenup shoot 120, Event coverage consultation 30, Photo editing review 30.

## Adding another industry

1. Add a `new(...)` entry to `Kinds` in `Services/Industries.cs`.
2. Add the label, starter service and staff label constants to `Text.cs`.
3. Add a new numbered migration that widens the `business_industry` CHECK list (never edit an applied one).
4. Update `tests/VeskaFlow.Web.Tests/IndustryTests.cs` and `docs/BUSINESS_RULES.md` section 18.1 in the same commit.

Ideas not yet added: laundry, repair shop, optical, driving school.
